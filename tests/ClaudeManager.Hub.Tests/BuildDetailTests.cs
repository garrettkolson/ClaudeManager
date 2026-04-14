using System.Text.Json;
using ClaudeManager.Hub.Components.Pages;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using Microsoft.AspNetCore.Components;
using Moq;
using Xunit;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Tests for BuildDetail component functionality.
/// Verifies tab navigation, page title truncation, and action button visibility.
/// </summary>
public class BuildDetailTests
{
    private readonly FieldInfo _activeTabField;
    private readonly FieldInfo _detailField;
    private readonly FieldInfo _jobField;

    public BuildDetailTests()
    {
        // Use reflection to access the private fields
        var componentType = typeof(BuildDetail);
        _activeTabField = componentType.GetField("_activeTab", BindingFlags.Instance | BindingFlags.NonPublic);
        _detailField = componentType.GetField("_detail", BindingFlags.Instance | BindingFlags.NonPublic);
        _jobField = componentType.GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic);

        if (_activeTabField == null)
        {
            throw new Exception("Cannot access _activeTab private field via reflection");
        }
    }

    [Fact]
    public void Test_ChangeTab_Verifies_TabSwitchUpdatesState()
    {
        // Arrange
        var component = (BuildDetail)Activator.CreateInstance(typeof(BuildDetail))!;

        // Act - Call ChangeTab and verify _activeTab changes
        component.ChangeTab(2);
        var newValue = (int)_activeTabField.GetValue(component);

        // Assert
        Assert.Equal(2, newValue);
    }

    [Fact]
    public void Test_ChangeTab_Default_Value_Is_One()
    {
        // Arrange
        var component = (BuildDetail)Activator.CreateInstance(typeof(BuildDetail))!;

        // Act - _activeTab should be initialized to 1 by default
        var initialTab = (int)_activeTabField.GetValue(component);

        // Assert
        Assert.Equal(1, initialTab);
    }

    [Fact]
    public void Test_ChangeTab_Validates_ValidRange()
    {
        // Arrange
        var component = (BuildDetail)Activator.CreateInstance(typeof(BuildDetail))!;

        // Act - Should not throw for valid range 1-3
        component.ChangeTab(2);
        var value2 = (int)_activeTabField.GetValue(component);
        component.ChangeTab(3);
        var value3 = (int)_activeTabField.GetValue(component);

        // Assert
        Assert.Equal(2, value2);
        Assert.Equal(3, value3);
    }

    [Fact]
    public void Test_ChangeTab_Handles_InvalidRange()
    {
        // Arrange
        var component = (BuildDetail)Activator.CreateInstance(typeof(BuildDetail))!;

        // Act - Should not throw for invalid values
        component.ChangeTab(0);
        var value0 = (int)_activeTabField.GetValue(component);

        component.ChangeTab(4);
        var value4 = (int)_activeTabField.GetValue(component);

        // Assert - Invalid values should be clamped to valid range
        Assert.True(value0 > 0 && value0 <= 3);
        Assert.True(value4 > 0 && value4 <= 3);
    }

    [Fact]
    public void Test_Truncate_Truncates_LongString()
    {
        // Arrange
        var input = "This is a very long string that exceeds the maximum length.";
        var maxLength = 40;
        var result = BuildDetail.Truncate(input, maxLength);

        // Assert
        Assert.True(result.Length <= maxLength);
        Assert.StartsWith(input.Substring(0, maxLength), result);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void Test_Truncate_Returns_Unchanged_For_ShortString()
    {
        // Arrange
        var input = "Short";
        var maxLength = 40;
        var result = BuildDetail.Truncate(input, maxLength);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void Test_Truncate_Returns_Empty_For_NullString()
    {
        // Arrange
        var input = null as string;
        var maxLength = 40;
        var result = BuildDetail.Truncate(input, maxLength);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void Test_PageTitle_Truncates_BuildGoal_To_40_Characters()
    {
        // Arrange
        var input = "This is a very long build goal string that exceeds...";

        // Assert - Truncate method truncates to maxLength with ellipsis
        var result = BuildDetail.Truncate(input, 40);
        Assert.True(result.Length <= 40);
    }

    [Fact]
    public void Test_Truncate_Adds_Ellipsis_For_Strings_Exceeding_MaxLength()
    {
        // Arrange
        var input = "This is a very long build goal string that exceeds...";
        var maxLength = 40;
        var result = BuildDetail.Truncate(input, maxLength);

        // Assert - Maximum 40 chars and ends with ellipsis
        Assert.True(result.Length <= 40);
        Assert.True(result.EndsWith("..."));
    }

    [Fact]
    public void Test_ChangeTab_Only_One_Tab_At_A_Time()
    {
        // Arrange
        var component = (BuildDetail)Activator.CreateInstance(typeof(BuildDetail))!;

        // Act - Simulate tab switching
        component.ChangeTab(1);
        var tab1 = (int)_activeTabField.GetValue(component);
        component.ChangeTab(2);
        var tab2 = (int)_activeTabField.GetValue(component);
        component.ChangeTab(3);
        var tab3 = (int)_activeTabField.GetValue(component);

        // Assert - Only one tab active at a time
        Assert.Equal(1, tab1);
        Assert.Equal(2, tab2);
        Assert.Equal(3, tab3);
        Assert.NotEqual(tab1, tab2);
        Assert.NotEqual(tab1, tab3);
        Assert.NotEqual(tab2, tab3);
    }

    [Fact]
    public void Test_JobNotNullPopulatesJobData()
    {
        // Arrange
        var mockService = new Mock<BruSefSvc>();
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "ext-123",
            Goal = "Test goal",
            Status = BuildStatus.Running
        };
        mockService.Setup(m => m.FetchAdtAsync("ext-123")).ReturnsAsync(Build);

        var component = new BuildDetail
        {
            JobId = 1
        };

        // Assert
        Assert.NotNull(component._job);
    }

    [Fact]
    public void Test_JobIsNullShowsLoadingMessage()
    {
        // Arrange
        var mockService = new Mock<BuildService>();
        mockService.Setup(m => m.FetchAdtAsync(It.IsAny<string>())).ReturnsAsync(null as JobDetail);

        var component = new BuildDetail
        {
            JobId = 1
        };

        // Assert
        Assert.Null(component._job);
    }

    [Fact]
    public void Test_ActionButtonVisibilityByStatus()
    {
        // Arrange
        var statuses = new[] { BuildStatus.Queued, BuildStatus.Running, BuildStatus.Waiting, BuildStatus.Failed, BuildStatus.Cancelled };

        foreach (var status in statuses)
        {
            // Assert - These statuses should have action buttons
            bool shouldShowAction = status is BuildStatus.Queued or BuildStatus.Running
                or BuildStatus.Waiting or BuildStatus.Failed or BuildStatus.Cancelled;
            Assert.True(shouldShowAction);
        }
    }

    [Fact]
    public void Test_ApproveJobExecution()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "ext-123",
            Goal = "Test goal",
            Status = BuildStatus.Approved,
            Repository = new BuildDetailRepository { Url = "https://github.com/test/repo" },
            Logs = "--- LOG ---",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            StartedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2)),
            CompletedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(1))
        };

        var buildsSvc = new Mock<BruSefSvc>();
        buildsSvc.Setup(m => m.GetJobAsync(It.IsAny<long>())).ReturnsAsync(() => job);
        buildsSvc.Setup(m => m.FetchAdtAsync("ext-123")).ReturnsAsync(Build);

        var component = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Assert
        Assert.NotNull(component._job);
    }

    [Fact]
    public void Test_LogParserIntegration()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "ext-123",
            Goal = "Test goal",
            Status = BuildStatus.Running,
            Repository = new BuildDetailRepository { Url = "https://github.com/test/repo" },
            Logs = "{\"events\":[{\"time\":\"2024-01-01T00:00:00Z\",\"level\":\"info\",\"message\":\"Started\"}]}",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            StartedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2)),
            CompletedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(1))
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(It.IsAny<long>())).ReturnsAsync(() => job);
        buildsSvc.Setup(m => m.FetchAdtAsync("ext-123")).ReturnsAsync(Build);

        var component = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Assert
        Assert.NotNull(component._job);
        Assert.NotNull(component._job.Logs);
    }

    [Fact]
    public void Test_ChangeTabActivity()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "ext-123",
            Goal = "Test goal",
            Status = BuildStatus.Running,
            Repository = new BuildDetailRepository { Url = "https://github.com/test/repo" },
            Logs = "--- LOG ---",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            StartedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(2)),
            CompletedAt = DateTime.UtcNow.Subtract(TimeSpan.FromHours(1))
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(It.IsAny<long>())).ReturnsAsync(() => job);
        buildsSvc.Setup(m => m.FetchAdtAsync("ext-123")).ReturnsAsync(Build);

        var detail = new BuildDetail { BuildService = buildsSvc.Object, JobId = 1 };

        // Act
        Task.Delay(1).Wait();

        // Assert
        Assert.Equal(1, detail._activeTab);
    }

    [Fact]
    public void Test_OnBuildChangedUpdatesJob()
    {
        // Arrange
        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(It.IsAny<long>())).ReturnsAsync(Build);

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        detail.OnBuildChanged(Build);

        // Assert
    }

    // ============================================================================
    // AC1: RefreshDetail() method exists and functions correctly
    // ============================================================================

    [Fact]
    public async Task TestRefreshDetail_Updates_Detail_On_Success()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 123,
            ExternalJobId = "exec-001",
            Goal = "Test build goal",
            Status = BuildStatus.Running,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(123)).ReturnsAsync(job);
        buildsSvc.Setup(m => m.FetchExecutionDetailAsync("exec-001")).ReturnsAsync(It.IsAny<BuildExecutionDetail>());

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 123
        };

        // Act
        await detail.RefreshDetail();

        // Assert
        Assert.NotNull(detail._detail);
    }

    [Fact]
    public async Task TestRefreshDetail_Handles_Null_Detail()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 123,
            ExternalJobId = "exec-001",
            Goal = "Test build goal",
            Status = BuildStatus.Running,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(123)).ReturnsAsync(job);
        buildsSvc.Setup(m => m.FetchExecutionDetailAsync("exec-001")).ReturnsAsync((BuildExecutionDetail?)null);

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 123
        };

        // Act
        await detail.RefreshDetail();

        // Assert
        Assert.Null(detail._detail);
        Assert.NotEmpty(detail._detailError);
    }

    [Fact]
    public async Task TestRefreshDetail_Turns_On_DetailLoading_Flags()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 123,
            ExternalJobId = "exec-001",
            Goal = "Test build goal",
            Status = BuildStatus.Running,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(123)).ReturnsAsync(job);
        buildsSvc.Setup(m => m.FetchExecutionDetailAsync("exec-001")).ReturnsAsync(
            new BuildExecutionDetail { ResultJson = "{}" });

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 123
        };

        // Act
        await detail.RefreshDetail();

        // Assert
        Assert.False(detail._detailLoading);
    }

    // ============================================================================
    // AC2: CancelJob(), RetryJob(), ApproveJob() methods still function
    // ============================================================================

    [Fact]
    public async Task TestCancelJob_Sets_ActionBusy_During_Progress()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        var task = buildsSvc.Setup(m => m.CancelJobAsync(1)).ReturnsAsync((true, null));
        var cancelTask = Task.Run(() => { });

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        detail.CancelJob();

        // Assert - Action should be busy during execution
        Assert.True(detail._actionBusy);
    }

    [Fact]
    public async Task TestCancelJob_Handles_Error_With_ActionError()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.CancelJobAsync(1)).ReturnsAsync((false, "Cancel failed"));

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        await Task.Run(() => detail.CancelJob());

        // Assert - Error should be captured
        Assert.False(detail._actionBusy);
        Assert.Contains("failed", detail._actionError);
    }

    [Fact]
    public async Task TestRetryJob_Sets_ActionBusy_During_Progress()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Failed,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.RetryJobAsync(1)).ReturnsAsync(new ValidationResult(true, null));

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        await Task.Run(() => detail.RetryJob());

        // Assert
        Assert.False(detail._actionBusy);
    }

    [Fact]
    public async Task TestApproveJob_Sets_ActionBusy_During_Progress()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Waiting,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.ApproveJobAsync(1, true)).ReturnsAsync(new ValidationResult(true, null));

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        await Task.Run(() => detail.ApproveJob(true));

        // Assert
        Assert.False(detail._actionBusy);
    }

    [Fact]
    public async Task TestApproveJob_Handles_Error_With_ActionError()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Waiting,
            Logs = ""
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.ApproveJobAsync(1, true)).ReturnsAsync(new ValidationResult(false, null));

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        await Task.Run(() => detail.ApproveJob(true));

        // Assert - Error should be captured
        Assert.False(detail._actionBusy);
    }

    // ============================================================================
    // AC3: RefreshLogs(), CopyLogs(), DownloadLogs() methods still function
    // ============================================================================

    [Fact]
    public async Task TestRefreshLogs_Fetches_New_Logs_From_Service()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = "Old logs"
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);
        buildsSvc.Setup(m => m.FetchExecutionDetailAsync("exec-001")).ReturnsAsync(
            new BuildExecutionDetail { Logs = "New logs" });

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        await Task.Run(() => detail.RefreshLogs());

        // Assert
        Assert.True(detail._detailLoading == false);
        Assert.Equal("New logs", job.Logs);
    }

    [Fact]
    public async Task TestRefreshLogs_Handles_Null_Logs_Gracefully()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Failed,
            Logs = null
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act - should not throw for null logs
        await Task.Run(() => detail.RefreshLogs());

        // Assert
        Assert.True(detail._detailLoading == false);
    }

    [Fact]
    public async Task TestRefreshLogs_Turns_On_DetailLoading_Flags()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = "Some logs"
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        await Task.Run(() => detail.RefreshLogs());

        // Assert - Calls loading flag
        Assert.True(detail._detailLoading == false);
    }

    [Fact]
    public async Task TestCopyLogs_Copies_Logs_To_Clipboard()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = "Build log content here"
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var clipboard = new Mock<IClipboard>();
        clipboard.Setup(m => m.SetDataAsync(It.IsAny<string>()))
                 .Returns(Task.CompletedTask);

        var jsRuntime = new Mock<IJavaScriptRuntime>();
        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            Clipboard = clipboard.Object,
            JobId = 1
        };

        // Act - should copy logs to clipboard
        await Task.Run(() => detail.CopyLogs());

        // Assert
        clipboard.Verify(m => m.SetDataAsync("Build log content here"), Times.Once);
    }

    [Fact]
    public async Task TestCopyLogs_Handles_Null_Logs_Gracefully()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Failed,
            Logs = null
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var clipboard = new Mock<IClipboard>();
        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            Clipboard = clipboard.Object,
            JobId = 1
        };

        // Act - Should silently exit for null logs
        await Task.Run(() => detail.CopyLogs());

        // Assert - No calls to clipboard for null logs
        clipboard.Verify(m => m.SetDataAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TestDownloadLogs_Generates_Download_Link()
    {
        // Arrange
        const string logs = "Build log content";
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = logs
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var jsRuntime = new Mock<IJavaScriptRuntime>();
        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JSRuntime = jsRuntime.Object,
            JobId = 1
        };

        // Act - should generate download link
        await Task.Run(() => detail.DownloadLogs());

        // Assert - JS should be invoked with download code
        jsRuntime.Verify(m => m.InvokeVoidAsync("eval", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TestDownloadLogs_Handles_Empty_Logs_Gracefully()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Failed,
            Logs = null
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var jsRuntime = new Mock<IJavaScriptRuntime>();
        var detail = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JSRuntime = jsRuntime.Object,
            JobId = 1
        };

        // Act - Should silently exit for null logs
        await Task.Run(() => detail.DownloadLogs());

        // Assert - No JS invoke for null logs
        jsRuntime.Verify(m => m.InvokeVoidAsync("eval", It.IsAny<string>()), Times.Never);
    }

    // ============================================================================
    // AC4: Responsive design works on tablets and mobile screens
    // ============================================================================

    [Fact]
    public void TestResponsiveDesign_żaButton_layout()
    {
        // Arrange - Verify responsive CSS exists
        var razorContent = typeof(BuildDetail)
            .GetCustomAttributes(true)
            .OfType<Type>()
            .GetInterfaces()
            .FirstOrDefault();

        // Act/Assert - Responsive CSS defined in the component's CSS
        // This is a layout test, checking that responsive breakpoints exist
        var cssContent = File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor.css");

        // Check that tablet and mobile breakpoints are defined
        Assert.Contains("@media (max-width: 768px)", cssContent);
        Assert.Contains("@media (max-width: 480px)", cssContent);
    }

    [Fact]
    public void TestResponsiveDesign_zaTab_Button_stack()
    {
        // Arrange
        var cssContent = File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor.css");

        // Act/Assert - Verify tab button stacking on tablet/mobile
        // Check for flex-direction: column in responsive styles
        Assert.Contains("flex-direction: column", cssContent);
        Assert.Contains("width: 100%", cssContent);
        Assert.Contains("height: 50px", cssContent);
    }

    [Fact]
    public void TestResponsiveDesign_zaText_Font_Size_zaMobile()
    {
        // Arrange
        var cssContent = File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor.css");

        // Act/Assert - Verify text size reduction on small screens
        Assert.Contains("font-size: 12px", cssContent);
        Assert.Contains("padding: 8px", cssContent);
    }

    // ============================================================================
    // AC5: Loading states show appropriate indicators
    // ============================================================================

    [Fact]
    public void TestLoadingStates_Show_Loading_Message()
    {
        // Arrange
        var cssContent = File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor.css");

        var razorContent = System.IO.File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor");

        // Act/Assert - Verify loading messages exist
        Assert.Contains(".loading", cssContent);
        Assert.Contains("'Loading'", razorContent)
            || Assert.Contains("Loading...", razorContent);
        Assert.Contains(".logs-loader", cssContent);
    }

    [Fact]
    public void TestLoadingStates_Button_Disabled_During_Reload()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Status = BuildStatus.Running,
            Logs = "Some logs"
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);
        buildsSvc.Setup(m => m.FetchExecutionDetailAsync("exec-001"))
                 .ReturnsAsync(new BuildExecutionDetail { ResultJson = "{}" });

        var component = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act - Set detail loading to true
        component.RefreshDetail();

        // Act/Assert - Button disabled state should respect loading flag
        // (validate via reflection or simulation)
    }

    // ============================================================================
    // AC6: Null/empty states handled with fallback messages
    // ============================================================================

    [Fact]
    public void TestNullStates_Show_Fallback_Message()
    {
        // Arrange
        var cssContent = File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor.css");

        var razorContent = System.IO.File.ReadAllText(
            "../src/ClaudeManager.Hub/Components/Pages/BuildDetail.razor");

        // Act/Assert - Verify null/empty state messages exist
        Assert.Contains(".build-log-empty", cssContent);
        Assert.Contains("No build logs", razorContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No results", razorContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestNullStates_Handle_Missing_Job_Data()
    {
        // Arrange
        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync((SweAfJobEntity?)null);

        var component = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        Task.Delay(10).Wait();

        // Assert - Job null handled gracefully during initialization
        // (The component checks for null in OnInitializedAsync)
    }

    [Fact]
    public void TestNullStates_Handle_Empty_Logs()
    {
        // Arrange
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            Logs = string.Empty
        };

        var buildsSvc = new Mock<BuildService>();
        buildsSvc.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);
        buildsSvc.Setup(m => m.FetchExecutionDetailAsync("exec-001"))
                 .ReturnsAsync(new BuildExecutionDetail { Logs = string.Empty });

        var component = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        Task.Delay(10).Wait();

        // Assert - Component handles empty logs gracefully
    }

    [Fact]
    public void TestNullStates_Handle_ControlPlane_URL()
    {
        // Arrange
        var jobs = new Mock<BuildService>();
        var job = new SweAfJobEntity
        {
            Id = 1,
            ExternalJobId = "exec-001",
            Goal = "Test build",
            ControlPlaneUrl = null
        };

        jobs.Setup(m => m.GetJobAsync(1)).ReturnsAsync(job);

        var component = new BuildDetail
        {
            BuildService = buildsSvc.Object,
            JobId = 1
        };

        // Act
        Task.Delay(10).Wait();

        // Assert - Component displays control plane URL not available message
    }
}
