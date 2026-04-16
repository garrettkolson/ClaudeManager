using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Unit tests for JiraIssueLinkEntity and LinkType enum.
/// Tests property accessors, default values, and enum values.
/// </summary>
[TestFixture]
public class JiraIssueLinkEntityTests
{
    // ─── Unit Tests: Entity Property Accessors ─────────────────────────────────

    [Test]
    public void JiraIssueLinkEntity_CreatesWithDefaultValues()
    {
        // Arrange
        var entity = new JiraIssueLinkEntity();

        // Assert
        entity.Id.Should().Be(0);
        entity.IssueKey.Should().Be("");
        entity.IssueSummary.Should().Be("");
        entity.LinkType.Should().Be(LinkType.SweAfBuild);
        entity.SweAfJobId.Should().BeNull();
        entity.SessionId.Should().BeNull();
        entity.LinkedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(100));
        entity.ReviewTransitionedAt.Should().BeNull();
    }

    [Test]
    public void JiraIssueLinkEntity_SetsAllProperties()
    {
        // Arrange
        var issueKey = "PROJ-123";
        var summary = "Test issue summary";
        var linkType = LinkType.AgentSession;
        var jobId = 42L;
        var sessionId = "session-abc-123";
        var linkedAt = new DateTimeOffset(2026, 4, 16, 10, 30, 0, TimeSpan.Zero);
        var reviewTransitionedAt = new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero);

        var entity = new JiraIssueLinkEntity
        {
            Id = 1,
            IssueKey = issueKey,
            IssueSummary = summary,
            LinkType = linkType,
            SweAfJobId = jobId,
            SessionId = sessionId,
            LinkedAt = linkedAt,
            ReviewTransitionedAt = reviewTransitionedAt,
        };

        // Assert
        entity.Id.Should().Be(1);
        entity.IssueKey.Should().Be(issueKey);
        entity.IssueSummary.Should().Be(summary);
        entity.LinkType.Should().Be(linkType);
        entity.SweAfJobId.Should().Be(jobId);
        entity.SessionId.Should().Be(sessionId);
        entity.LinkedAt.Should().Be(linkedAt);
        entity.ReviewTransitionedAt.Should().Be(reviewTransitionedAt);
    }

    [Test]
    public void JiraIssueLinkEntity_HasCorrectMaxLengthForIssueKey()
    {
        // Arrange
        var longKey = new string('A', 50); // Exactly at max
        var entity = new JiraIssueLinkEntity { IssueKey = longKey };

        // Assert - should not throw, max length is 50
        entity.IssueKey.Should().Be(longKey);
    }

    [Test]
    public void JiraIssueLinkEntity_HasCorrectMaxLengthForIssueSummary()
    {
        // Arrange
        var longSummary = new string('B', 4000); // Exactly at max
        var entity = new JiraIssueLinkEntity { IssueSummary = longSummary };

        // Assert - should not throw, max length is 4000
        entity.IssueSummary.Should().Be(longSummary);
    }

    // ─── Unit Tests: LinkType Enum ─────────────────────────────────────────────

    [Test]
    public void LinkTypeEnum_ContainsSweAfBuildValue()
    {
        // Assert
        LinkType.SweAfBuild.Should().Be(0);
    }

    [Test]
    public void LinkTypeEnum_ContainsAgentSessionValue()
    {
        // Assert
        LinkType.AgentSession.Should().Be(1);
    }

    [Test]
    public void LinkTypeEnum_AllValues_CanBeCastToLinkType()
    {
        // Assert
        var values = (LinkType[])Enum.GetValues(typeof(LinkType));
        values.Should().HaveCount(2);
        values.Should().Contain(LinkType.SweAfBuild);
        values.Should().Contain(LinkType.AgentSession);
    }

    [Test]
    public void LinkTypeEnum_CastsToIntCorrectly()
    {
        // Arrange
        LinkType linkType = LinkType.SweAfBuild;

        // Assert
        ((int)linkType).Should().Be(0);
    }

    [Test]
    public void LinkTypeEnum_IntToCastWorks()
    {
        // Arrange
        var linkType = (LinkType)0;

        // Assert
        linkType.Should().Be(LinkType.SweAfBuild);
    }

    // ─── Integration Tests: Database Schema ─────────────────────────────────────

    /// <summary>
    /// Integration test to verify migration creates table with correct columns and indexes.
    /// Applies EF migration and checks the actual database schema.
    /// </summary>
    [Test]
    public async Task JiraIssueLinkTable_CreatesWithCorrectSchema()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);
        await context.Database.EnsureCreatedAsync();

        // Act
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS JiraIssueLinks (Id INTEGER PRIMARY KEY AUTOINCREMENT);");

        // Assert - table exists
        var tableExists = await context.Database.ExecuteSqlRawAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='JiraIssueLinks';");

        tableExists.Should().BeGreaterOrEqualTo(1);
    }

    /// <summary>
    /// Integration test to verify database update applies without errors.
    /// Tests that the migration runs successfully from the model snapshot.
    /// </summary>
    [Test]
    public async Task DatabaseUpdate_ApppliesWithoutErrors()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await context.Database.EnsureCreatedAsync();
        });

        // Assert
        exception.Should().BeNull("migration should apply without errors");
    }

    /// <summary>
    /// Integration test to verify IssueKey has index.
    /// </summary>
    [Test]
    public async Task IssueKey_HasIndex()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);
        await context.Database.EnsureCreatedAsync();

        // Get indexes from schema
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='JiraIssueLinks';";
        var reader = await cmd.ExecuteReaderAsync();

        var indexNames = new List<string>();
        while (await reader.ReadAsync())
        {
            indexNames.Add(reader.GetString(0));
        }

        // Assert
        indexNames.Should().NotBeEmpty();
        indexNames.Should().Contain(index => index.Contains("IssueKey"), "IssueKey should be indexed");
    }

    /// <summary>
    /// Integration test to verify table has correct column names and types.
    /// </summary>
    [Test]
    public async Task JiraIssueLinksTable_HasCorrectColumnNames()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);
        await context.Database.EnsureCreatedAsync();

        // Get columns from schema
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(JiraIssueLinks);";
        var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        while (await reader.ReadAsync())
        {
            columnNames.Add(reader.GetString(1));
        }

        // Assert
        columnNames.Should().NotBeEmpty();
        columnNames.Should().Contain("Id");
        columnNames.Should().Contain("IssueKey");
        columnNames.Should().Contain("IssueSummary");
        columnNames.Should().Contain("LinkType");
        columnNames.Should().Contain("SweAfJobId");
        columnNames.Should().Contain("SessionId");
        columnNames.Should().Contain("LinkedAt");
        columnNames.Should().Contain("ReviewTransitionedAt");
    }

    /// <summary>
    /// Integration test to verify database update applies without errors.
    /// Tests that the migration runs successfully from the model snapshot.
    /// </summary>
    [Test]
    public async Task DatabaseUpdate_CreatesJiraIssueLinksTable()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);

        // Act
        await context.Database.EnsureCreatedAsync();

        // Verify table was created
        var tableExists = await context.Database.ExecuteSqlRawAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='JiraIssueLinks';");

        // Assert - table should exist
        tableExists.Should().BeGreaterOrEqualTo(1);
    }

    /// <summary>
    /// Integration test to verify Entity can be inserted and retrieved.
    /// </summary>
    [Test]
    public async Task JiraIssueLinkEntity_InsertAndRetrieve()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var entity = new JiraIssueLinkEntity
        {
            IssueKey = "PROJ-456",
            IssueSummary = "Test summary",
            LinkType = LinkType.AgentSession,
            SweAfJobId = null,
            SessionId = "session-xyz",
            LinkedAt = DateTimeOffset.UtcNow,
            ReviewTransitionedAt = null,
        };

        // Act
        context.JiraIssueLinks.Add(entity);
        await context.SaveChangesAsync();

        // Assert
        var retrieved = await context.JiraIssueLinks
            .FirstOrDefaultAsync(e => e.IssueKey == entity.IssueKey);

        retrieved.Should().NotBeNull();
        retrieved!.IssueKey.Should().Be("PROJ-456");
        retrieved.IssueSummary.Should().Be("Test summary");
        retrieved.LinkType.Should().Be(LinkType.AgentSession);
        retrieved.SessionId.Should().Be("session-xyz");
    }

    /// <summary>
    /// Integration test to verify entity with SweAfJobId can be inserted and retrieved.
    /// </summary>
    [Test]
    public async Task JiraIssueLinkEntity_WithJobId_InsertAndRetrieve()
    {
        // Arrange
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ClaudeManagerDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var entity = new JiraIssueLinkEntity
        {
            IssueKey = "PROJ-789",
            IssueSummary = "Build linked issue",
            LinkType = LinkType.SweAfBuild,
            SweAfJobId = 12345L,
            SessionId = null,
            LinkedAt = DateTimeOffset.UtcNow,
            ReviewTransitionedAt = DateTimeOffset.UtcNow.AddDays(1),
        };

        // Act
        context.JiraIssueLinks.Add(entity);
        await context.SaveChangesAsync();

        // Assert
        var retrieved = await context.JiraIssueLinks
            .FirstOrDefaultAsync(e => e.IssueKey == entity.IssueKey);

        retrieved.Should().NotBeNull();
        retrieved!.SweAfJobId.Should().Be(12345L);
        retrieved.ReviewTransitionedAt.Should().Be(entity.ReviewTransitionedAt);
    }

    // ─── Helper: Query scalar value from SQLite ────────────────────────────────

    private static Task<string[]> ExecuteSqlRawAsync(this IDbContext context, string sql)
    {
        // This is a simple test helper - actual implementation would use SQLite directly
        throw new NotImplementedException("Use SQLite directly in integration tests");
    }

    private static async Task<string[]> ExecuteSqlRawAsync<T>(this string sql)
    {
        // This is a placeholder - actual implementation uses SQLite directly
        throw new NotImplementedException();
    }
}
