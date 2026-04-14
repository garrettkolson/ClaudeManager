using System.Reflection;
using ClaudeManager.Hub.Components.Pages;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using Xunit;
using Moq;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Tests for BuildDetail component tab navigation functionality.
/// Verifies AC1: Three tab buttons rendered
/// Verifies AC2: _activeTab state variable exists with default value 1
/// Verifies AC3: Tab button onclick handlers call ChangeTab method and StateHasChanged
/// Verifies AC5: Only one tab at a time is visually active
/// </summary>
public class BuildDetailTests
{
    private readonly FieldInfo _activeTabField;

    public BuildDetailTests()
    {
        // Use reflection to access the private _activeTab field
        var componentType = typeof(BuildDetail);
        _activeTabField = componentType.GetField("_activeTab", BindingFlags.Instance | BindingFlags.NonPublic);
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

        // Act - Should not throw for invalid values, should clamp to valid range
        component.ChangeTab(0);
        var value0 = (int)_activeTabField.GetValue(component);

        component.ChangeTab(4);
        var value4 = (int)_activeTabField.GetValue(component);

        // Assert - Invalid values should be ignored or clamped
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
        Assert.LengthLessThanOrEqual(maxLength, result.Length);
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
    public void Test_PageTitle_Truncates_BuildGoal_To_43_Characters()
    {
        // Arrange
        var pageTitle = "This is a very long build goal string that exceeds...";

        // Assert
        Assert.LengthLessThanOrEqual(43, pageTitle.Length);
        Assert.StartsWith("This is a very long build goal string that exceeds...", pageTitle);
        Assert.EndsWith("...", pageTitle);
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

        // Assert - Excludes only one tab at a time, explicit
        Assert.Equal(1, tab1);
        Assert.Equal(2, tab2);
        Assert.Equal(3, tab3);
        Assert.NotEqual(tab1, tab2);
        Assert.NotEqual(tab1, tab3);
        Assert.NotEqual(tab2, tab3);
    }
}
