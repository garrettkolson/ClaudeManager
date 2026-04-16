using ClaudeManager.Hub.Services.Jira;
using FluentAssertions;
using NUnit.Framework;
using System.Text.Json;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Unit tests for the Jira DTOs and ToPlainText helper covering:
/// - Null input, plain string, ADF array with Text blocks
/// - ADF with nested Content arrays
/// - ADF with ContentMap.Text
/// - JiraIssue.Clone() deep copy behavior
/// </summary>
[TestFixture]
public class JiraDtosTests
{
    #region ToPlainText Tests

    [Test]
    public void ToPlainText_with_null_input_ShouldReturnEmptyString()
    {
        // Act
        var result = JiraDtosHelpers.ToPlainText(null);

        // Assert
        result.Should().Be("");
    }

    [Test]
    public void ToPlainText_with_plain_string_ShouldReturnOriginalString()
    {
        // Arrange
        var plainText = "This is a plain text description without ADF formatting.";

        // Act
        var result = JiraDtosHelpers.ToPlainText(plainText);

        // Assert
        result.Should().Be(plainText);
    }

    [Test]
    public void ToPlainText_with_ADF_array_having_Text_blocks_ShouldExtractText()
    {
        // Arrange - ADF with simple Text blocks
        var adfJson = @"[
            {
                ""type"": ""paragraph"",
                ""text"": ""First paragraph""
            },
            {
                ""type"": ""paragraph"",
                ""text"": ""Second paragraph""
            },
            {
                ""type"": ""paragraph"",
                ""text"": ""Third paragraph""
            }
        ]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("First paragraph\nSecond paragraph\nThird paragraph");
    }

    [Test]
    public void ToPlainText_with_ADF_having_nested_Content_arrays_ShouldExtractInlineText()
    {
        // Arrange - ADF with nested Content arrays
        var adfJson = @"[
            {
                ""type"": ""paragraph"",
                ""content"": [
                    {
                        ""type"": ""text"",
                        ""text"": ""Hello ""
                    },
                    {
                        ""type"": ""text"",
                        ""text"": ""World""
                    }
                ]
            },
            {
                ""type"": ""paragraph"",
                ""content"": [
                    {
                        ""type"": ""text"",
                        ""text"": ""This is a test""
                    }
                ]
            }
        ]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("Hello World\nThis is a test");
    }

    [Test]
    public void ToPlainText_with_ADF_having_ContentMap_Text_ShouldExtractText()
    {
        // Arrange - ADF with ContentMap.Text structure
        var adfJson = @"[
            {
                ""type"": ""paragraph"",
                ""contentMap"": {
                    ""text"": [
                        {
                            ""type"": ""text"",
                            ""text"": ""ContentMap paragraph 1""
                        }
                    ]
                }
            },
            {
                ""type"": ""paragraph"",
                ""contentMap"": {
                    ""text"": [
                        {
                            ""type"": ""text"",
                            ""text"": ""ContentMap paragraph 2""
                        }
                    ]
                }
            }
        ]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("ContentMap paragraph 1\nContentMap paragraph 2");
    }

    [Test]
    public void ToPlainText_with_mixed_ADF_blocks_ShouldHandleAllTypes()
    {
        // Arrange - ADF with mixed block types
        var adfJson = @"[
            {
                ""type"": ""paragraph"",
                ""text"": ""Text block""
            },
            {
                ""type"": ""paragraph"",
                ""content"": [
                    {
                        ""type"": ""text"",
                        ""text"": ""Content block""
                    }
                ]
            },
            {
                ""type"": ""paragraph"",
                ""contentMap"": {
                    ""text"": [
                        {
                            ""type"": ""text"",
                            ""text"": ""ContentMap block""
                        }
                    ]
                }
            }
        ]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("Text block\nContent block\nContentMap block");
    }

    [Test]
    public void ToPlainText_with_JsonElement_input_ShouldWorkCorrectly()
    {
        // Arrange
        var adfJson = @"[
            {
                ""type"": ""paragraph"",
                ""text"": ""JSON element test""
            }
        ]";

        var element = JsonSerializer.Deserialize<JsonElement>(adfJson);

        // Act
        var result = JiraDtosHelpers.ToPlainText(element);

        // Assert
        result.Should().Be("JSON element test");
    }

    [Test]
    public void ToPlainText_with_empty_array_ShouldReturnEmptyString()
    {
        // Arrange
        var adfJson = "[]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("");
    }

    [Test]
    public void ToPlainText_with_empty_block_ShouldHandleGracefully()
    {
        // Arrange
        var adfJson = @"[{""type"": ""paragraph""}]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("");
    }

    [Test]
    public void ToPlainText_with_empty_inline_ShouldHandleGracefully()
    {
        // Arrange
        var adfJson = @"[{""type"": ""paragraph"", ""content"": []}]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(adfJson);

        // Assert
        result.Should().Be("");
    }

    [Test]
    public void ToPlainText_with_json_array_not_ADF_ShouldReturnOriginal()
    {
        // Arrange - JSON array that is not ADF format
        var jsonNotAdf = @"[{""key"": ""value""}, {""another"": ""item""}]";

        // Act
        var result = JiraDtosHelpers.ToPlainText(jsonNotAdf);

        // Assert
        result.Should().Be(jsonNotAdf);
    }

    #endregion

    #region JiraIssue Clone Tests

    [Test]
    public void Clone_ShouldCreateDeepCopyOfAllProperties()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-123",
            Summary = "Test issue summary",
            Description = "Test description",
            Status = new JiraStatus { Id = "1", Name = "Open", StatusCategory = "To Do" },
            Issuetype = "Story",
            Priority = "High",
            Assignee = "john.doe@example.com",
            Labels = ["bug", "priority-high"],
            StoryPoints = 5,
            Url = "https://jira.example.com/browse/PROJ-123"
        };

        // Act
        var cloned = original.Clone();

        // Assert - basic properties should be equal
        cloned.Key.Should().Be("PROJ-123");
        cloned.Summary.Should().Be("Test issue summary");
        cloned.Description.Should().Be("Test description");
        cloned.Issuetype.Should().Be("Story");
        cloned.Priority.Should().Be("High");
        cloned.Assignee.Should().Be("john.doe@example.com");
        cloned.StoryPoints.Should().Be(5);
        cloned.Url.Should().Be("https://jira.example.com/browse/PROJ-123");

        // Assert - Status should be different object
        cloned.Status.Should().Be(original.Status);
        cloned.Status.Should().NotBeSameAs(original.Status);
    }

    [Test]
    public void Clone_ShouldCreateDeepCopyOfLabelsArray()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-456",
            Labels = ["label1", "label2", "label3"]
        };

        // Act
        var cloned = original.Clone();

        // Assert - Labels should have same content but different reference
        cloned.Labels.Should().HaveCount(3);
        cloned.Labels[0].Should().Be("label1");
        cloned.Labels[1].Should().Be("label2");
        cloned.Labels[2].Should().Be("label3");
        cloned.Labels.Should().NotBeSameAs(original.Labels);

        // Modify cloned labels
        cloned.Labels.Add("label4");

        // Original should be unchanged
        original.Labels.Should().HaveCount(3);
        original.Labels.Should().NotContain("label4");
    }

    [Test]
    public void Clone_ShouldHandleNullStatus()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-789",
            Status = null
        };

        // Act
        var cloned = original.Clone();

        // Assert
        cloned.Status.Should().BeNull();
    }

    [Test]
    public void Clone_ShouldHandleNullDescription()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-789",
            Description = null
        };

        // Act
        var cloned = original.Clone();

        // Assert
        cloned.Description.Should().BeNull();
    }

    [Test]
    public void Clone_ShouldHandleNullAssignee()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-789",
            Assignee = null
        };

        // Act
        var cloned = original.Clone();

        // Assert
        cloned.Assignee.Should().BeNull();
    }

    [Test]
    public void Clone_ShouldHandleEmptyLabels()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-789",
            Labels = new List<string>()
        };

        // Act
        var cloned = original.Clone();

        // Assert
        cloned.Labels.Should().BeEmpty();
    }

    [Test]
    public void Clone_ShouldHandleNullStoryPoints()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-789",
            StoryPoints = null
        };

        // Act
        var cloned = original.Clone();

        // Assert
        cloned.StoryPoints.Should().BeNull();
    }

    [Test]
    public void Clone_ShouldNotReferenceOriginalCollections()
    {
        // Arrange
        var original = new JiraIssue
        {
            Key = "PROJ-999",
            Labels = ["a", "b", "c"]
        };

        // Act
        var cloned = original.Clone();

        // Assert - modifying clone should not affect original
        cloned.Labels.RemoveAt(0);

        original.Labels.Should().HaveCount(3);
        original.Labels[0].Should().Be("a");
    }

    #endregion
}
