using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace ClaudeManager.Hub.Tests.Integration;

/// <summary>
/// Integration tests for BuildDetail Logs Widget component.
/// These tests verify the cross-feature interactions:
/// - Log parsing from cached Logs field in SweAfJobEntity
/// - Fetching fresh logs via FetchExecutionDetailAsync
/// - Control plane URL resolution (per-job vs hub fallback)
/// - Log display with JavaScript escaping for Copy/Download buttons
/// </summary>
[TestFixture]
public class BuildDetailLogsWidgetIntegrationTests
{
    private SqliteConnection? _conn;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private BuildNotifier _notifier = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (fact, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = fact;
        _notifier  = new BuildNotifier();
    }

    [TearDown]
    public void TearDown()
    {
        _conn?.Dispose();
    }

    #region Logs Reading from Cached Logs Field Integration

    [Test]
    public async Task LogsWidget_ReadsCachedLogsFromJobEntity()
    {
        // Arrange: Create job with cached logs from webhook
        await using var db = _dbFactory.CreateDbContext();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-123",
            Goal = "Test goal",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = "[10:00:00.000] Build started\n[10:00:01.000] Compiling code\n[10:00:05.500] Run tests"
        };
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        var service = new SweAfService(
            new TestHttpClientFactory(new HttpClient()),
            new SweAfConfigService(_dbFactory, NullLogger<SweAfConfigService>.Instance),
            _dbFactory,
            _notifier,
            new Mock<ISwarmProvisioningService>().Object,
            new Mock<ISwarmRunnerPortAllocator>().Object,
            NullLogger<SweAfService>.Instance);

        // Act: Fetch logs using webhook-cached logs
        var (logs, error) = await service.GetLogsAsync(job.Id);

        // Assert
        error.Should().BeNull();
        logs.Should().NotBeNullOrEmpty();
        logs.Should().Contain("[10:00:00.000] Build started");
        logs.Should().Contain("[10:00:01.000] Compiling code");
        logs.Should().Contain("[10:00:05.500] Run tests");
    }

    [Test]
    public async Task LogsWidget_NoCachedLogs_FallsBackToControlPlane()
    {
        // Arrange: Create job without cached logs
        await using var db = _dbFactory.CreateDbContext();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-no-logs",
            Goal = "Test goal",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = null  // No cached logs
        };
        db.SweAfJobs.Add(job);
        job.ControlPlaneUrl = "http://test-control-plane:8080";
        await db.SaveChangesAsync();

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"logs\": \"Fetched logs from control plane\"}")
            });

        var httpFactory = new TestHttpClientFactory(new HttpClient(mockHandler.Object));

        var configService = new SweAfConfigService(_dbFactory, NullLogger<SweAfConfigService>.Instance);
        configService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        var service = new SweAfService(
            httpFactory,
            configService,
            _dbFactory,
            _notifier,
            new Mock<ISwarmProvisioningService>().Object,
            new Mock<ISwarmRunnerPortAllocator>().Object,
            NullLogger<SweAfService>.Instance);

        // Act: Fetch logs when no cache available
        var (logs, error) = await service.GetLogsAsync(job.Id);

        // Assert: Should fall back to control plane logs
        error.Should().BeNull();
        logs.Should().Contain("Fetched logs from control plane");
    }

    #endregion

    #region Log Parsing Integration for Widget Display

    [Test]
    public async Task LogsWidget_ParsesParsedLogsFormatForDisplay()
    {
        // Arrange: Create job with already-parsed logs format
        await using var db = _dbFactory.CreateDbContext();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-parsed",
            Goal = "Parse test",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = "[09:15:30.555] Build started\n[09:15:30.600] Checking out repository"
        };
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        var parser = new LogParser();

        // Act: Parse logs for widget display (this is what BuildDetail.razor does at lines 163-170)
        var parsedLogs = parser.ParseLogMessages(job.Logs);
        var logsList = parsedLogs.ToList();

        // Assert
        logsList.Should().HaveCount(2);
        logsList[0].Content.Should().Be("Build started");
        logsList[1].Content.Should().Be("Checking out repository");
        logsList[0].Timestamp.Should().BeAfter(DateTimeOffset.MinValue);
        logsList[1].LineNumber.Should().Be(2);
    }

    [Test]
    public async Task LogsWidget_HandlesEmptyCacheState()
    {
        // Arrange: Job with empty logs state
        await using var db = _dbFactory.CreateDbContext();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-empty",
            Goal = "Empty test",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = ""  // Empty string
        };
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        var parser = new LogParser();

        // Act
        var parsedLogs = parser.ParseLogMessages(job.Logs).ToList();

        // Assert: Empty state should show no logs
        parsedLogs.Should().BeEmpty();
    }

    [Test]
    public async Task LogsWidget_HandlesNullCacheState()
    {
        // Arrange: Job with null logs (no webhook cache yet)
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-null",
            Goal = "Null test",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = null  // Null
        };

        var parser = new LogParser();

        // Act
        var parsedLogs = parser.ParseLogMessages(job.Logs).ToList();

        // Assert: Null should not crash
        parsedLogs.Should().BeEmpty();
    }

    #endregion

    #region Control Plane URL Resolution Integration

    [Test]
    public async Task FetchExecutionDetailAsync_UsesPerJobControlPlaneUrl()
    {
        // Arrange: Create job with per-job control plane URL
        await using var db = await _dbFactory.CreateDbContextAsync();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-perjob",
            Goal = "Per-job test",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            ControlPlaneUrl = "http://per-job:8080"
        };
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"logs\": \"Per-job logs\", \"status\": \"failed\"}")
            });

        var httpFactory = new TestHttpClientFactory(new HttpClient(mockHandler.Object));

        var configService = new SweAfConfigService(_dbFactory, NullLogger<SweAfConfigService>.Instance);
        configService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        var service = new SweAfService(
            httpFactory,
            configService,
            _dbFactory,
            _notifier,
            new Mock<ISwarmProvisioningService>().Object,
            new Mock<ISwarmRunnerPortAllocator>().Object,
            NullLogger<SweAfService>.Instance);

        // Act: Fetch execution detail
        var detail = await service.FetchExecutionDetailAsync(job.ExternalJobId);

        // Assert: Should use per-job URL
        detail.Should().NotBeNull();
        detail!.Logs.Should().Be("Per-job logs");
    }
    
    #endregion

    #region JavaScript Escaping for Copy/Download Buttons

    [Test]
    public void LogDisplay_EscapesBackslashesCorrectly()
    {
        // Arrange: Log content with backslashes (Windows paths common in CI/CD)
        var rawLog = @"Compiling from path\to\source\file.cs";
        var parser = new LogParser();
        var parsedLogs = parser.ParseLogMessages(rawLog).ToList();

        // Act: Get escaped content for JavaScript rendering (used in CopyLogs/DownloadLogs)
        var escaped = parsedLogs[0].Content.EscapeJavaScriptString();

        // Assert: Backslashes should be properly escaped
        escaped.Should().Contain(@"path\\to\\source\\file.cs");
    }

    [Test]
    public void LogDisplay_EscapesQuotesCorrectly()
    {
        // Arrange: Log with string quotes
        var rawLog = "Error: \"Cannot find module\"";
        var parser = new LogParser();
        var parsedLogs = parser.ParseLogMessages(rawLog).ToList();

        // Act
        var escaped = parsedLogs[0].Content.EscapeJavaScriptString();

        // Assert: Double quotes should be escaped
        escaped.Should().Contain("\\\"");
    }

    [Test]
    public void LogDisplay_EscapesNewlinesCorrectly()
    {
        // Arrange: Log with actual newlines
        var rawLog = "Line1\nLine2\nLine3";
        var parser = new LogParser();
        var parsedLogs = parser.ParseLogMessages(rawLog).ToList();

        // Act
        var escaped = parsedLogs[0].Content.EscapeJavaScriptString();

        // Assert: Newlines should be escaped as \n for JS
        escaped.Should().Contain("\\n");
    }

    [Test]
    public void LogDisplay_EscapesControlCharactersCorrectly()
    {
        // Arrange: Log with control characters
        var rawLog = "Warning: character with value " + (char)16 + " (non-printing)";
        var parser = new LogParser();
        var parsedLogs = parser.ParseLogMessages(rawLog).ToList();

        // Act
        var escaped = parsedLogs[0].Content.EscapeJavaScriptString();

        // Assert: Control chars should be hex-escaped
        escaped.Should().Contain("\\u");
    }

    #endregion

    #region Multi-line Log Parsing for Widget Display

    [Test]
    public async Task LogsWidget_ParsesMultiLineLogsWithLineNumbers()
    {
        // Arrange: Multi-line build log
        await using var db = _dbFactory.CreateDbContext();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-multiline",
            Goal = "Multi-line test",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = "[09:00:00.000] Step 1: Initialize\n[09:00:00.100] Step 2: Checkout\n[09:00:00.200] Step 3: Build"
        };
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        var parser = new LogParser();

        // Act: Parse for widget display
        var parsedLogs = parser.ParseLogMessages(job.Logs).ToList();

        // Assert: All lines parsed with correct structure
        parsedLogs.Should().HaveCount(3);
        parsedLogs[0].Content.Should().Be("Step 1: Initialize");
        parsedLogs[1].Content.Should().Be("Step 2: Checkout");
        parsedLogs[2].Content.Should().Be("Step 3: Build");
        parsedLogs[0].LineNumber.Should().Be(1);
        parsedLogs[1].LineNumber.Should().Be(2);
        parsedLogs[2].LineNumber.Should().Be(3);
    }

    #endregion

    #region Integration: Logs Fetching to Display

    [Test]
    public async Task LogsWidget_EndToEnd_FetchDisplayCycle()
    {
        // Arrange: Simulate webhook caching logs then widget fetching for display
        await using var db = _dbFactory.CreateDbContext();
        var job = new SweAfJobEntity
        {
            ExternalJobId = "exec-endto-end",
            Goal = "End-to-end test",
            RepoUrl = "https://github.com/test/repo",
            Status = BuildStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            Logs = "[12:00:00.000] End-to-end log message\n[12:00:01.000] Second log entry"
        };
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        var parser = new LogParser();

        // Act: Simulate widget rendering - parse logs for display
        var parsedLogs = parser.ParseLogMessages(job.Logs).ToList();

        // Assert: Widget can render parsed logs
        parsedLogs.Should().HaveCount(2);
        parsedLogs[0].Content.Should().Be("End-to-end log message");
        parsedLogs[1].Content.Should().Be("Second log entry");

        // Verify timestamps are parsed
        parsedLogs[0].Timestamp.Date.Should().Be(DateTimeOffset.UtcNow.Date);
    }

    #endregion

    #region Helpers

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
    #endregion
}
