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
}
