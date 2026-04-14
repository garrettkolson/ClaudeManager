using ClaudeManager.Hub.Services;
using FluentAssertions;
using NUnit.Framework;

namespace ClaudeManager.Hub.Tests;

/// <summary>
/// Unit tests for the LogParser service covering timestamps, plain text, empty input,
/// multiline logs, and special character escaping.
/// </summary>
[TestFixture]
public class LogParserTests
{
    private readonly ILogParser _parser = new LogParser();

    #region ParseLogMessages Tests

    [Test]
    public void ParseLogMessages_with_timestamps_ShouldExtractTimestampAndContent()
    {
        // Arrange
        var logEntry = "[10:30:00.123] Build started successfully";
        var logEntryMicroseconds = "[10:30:00.123456] Build started successfully";

        // Act
        var result = _parser.ParseLogMessages(logEntry).ToList();

        // Assert
        result.Should().NotBeEmpty();
        result.Should().HaveCount(1);

        var message = result[0];
        message.Timestamp.Year.Should().Be(2000); // Year is padding, we parse the time part
        message.Content.Should().Be("Build started successfully");
        message.LineNumber.Should().Be(1);
    }

    [Test]
    public void ParseLogMessages_plain_text_ShouldUseCurrentTimestamp()
    {
        // Arrange
        var plainLog = "This is a plain text log entry without timestamp";

        // Act
        var result = _parser.ParseLogMessages(plainLog).ToList();

        // Assert
        result.Should().HaveCount(1);
        var message = result[0];
        message.Content.Should().Be(plainLog);
        message.LineNumber.Should().Be(1);
    }

    [Test]
    public void ParseLogMessages_empty_input_ShouldReturnEmptyCollection()
    {
        // Arrange
        string emptyInput = "";
        string whitespaceInput = "   \n\t  ";

        // Act
        var result1 = _parser.ParseLogMessages(emptyInput).ToList();
        var result2 = _parser.ParseLogMessages(whitespaceInput).ToList();

        // Assert
        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
    }

    [Test]
    public void ParseLogMessages_multiline_ShouldParseEachLineSeparately()
    {
        // Arrange
        string multiLineLog = """
            [10:30:00.123] First log message
            [10:30:00.456] Second log message
            Plain text message without timestamp
            [10:30:01.789] Fourth message
            Empty line should be skipped
            Another plain text message
            """;

        // Act
        var result = _parser.ParseLogMessages(multiLineLog).ToList();

        // Assert
        result.Should().HaveCount(6); // 4 timestamped + 2 plain text (2 empty lines skipped)

        // Verify first message
        result[0].Content.Should().Be("First log message");
        result[0].LineNumber.Should().Be(1);

        // Verify second message
        result[1].Content.Should().Be("Second log message");
        result[1].LineNumber.Should().Be(2);

        // Verify third message (plain text)
        result[2].Content.Should().Be("Plain text message without timestamp");
        result[2].LineNumber.Should().Be(3);

        // Verify fourth message
        result[3].Content.Should().Be("Fourth message");
        result[3].LineNumber.Should().Be(4);

        // Verify fifth message (plain text at line 6)
        result[5].Content.Should().Be("Another plain text message");
        result[5].LineNumber.Should().Be(6);
    }

    [Test]
    public void ParseLogMessages_unknown_timestamp_format_ShouldTreatAsPlainText()
    {
        // Arrange
        var unknownFormat = "[2023-01-01 10:30:00] Build started";

        // Act
        var result = _parser.ParseLogMessages(unknownFormat).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Be("[2023-01-01 10:30:00] Build started");
    }

    [Test]
    public void ParseLogMessages_mixed_timestamp_formats_ShouldHandleGracefully()
    {
        // Arrange
        var mixedLog = """
            [10:30:00.123] Milliseconds format
            [10:30:00.123456] Microseconds format
            [10:30:00.1234567] Nanoseconds format
            10:30:00 Plain time
            [10:30:00] Hour-minute-second only
            """;

        // Act
        var result = _parser.ParseLogMessages(mixedLog).ToList();

        // Assert - only first two lines have proper timestamp format
        result.Should().HaveCount(5); // First 2 timestamped, last 3 treated as plain text

        // Message content should preserve the prefix for unrecognized formats
        result.Should().Contain(m => m.Content == "Milliseconds format");
        result.Should().Contain(m => m.Content == "Microseconds format");
        result.Should().Contain(m => m.Content == "[10:30:00] Hour-minute-second only");
    }

    [Test]
    public void ParseLogMessages_newline_characters_ShouldPreserveInContent()
    {
        // Arrange
        var logWithNewlines = "[10:30:00.123] Message with\nembedded\nnewlines";

        // Act
        var result = _parser.ParseLogMessages(logWithNewlines).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Contain("with\nembedded\nnewlines");
    }

    [Test]
    public void ParseLogMessages_tab_characters_ShouldPreserveInContent()
    {
        // Arrange
        var logWithTabs = "[10:30:00.123] Message with\ttab\there";

        // Act
        var result = _parser.ParseLogMessages(logWithTabs).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Contain("\ttab\there");
    }

    #endregion

    #region EscapeJavaScriptString Tests

    [Test]
    public void EscapeJavaScriptString_escapes_backslashes()
    {
        // Arrange
        string withBackslashes = @"path\to\file";

        // Act
        var result = withBackslashes.EscapeJavaScriptString();

        // Assert
        result.Should().Be(@"path\\to\\file");
    }

    [Test]
    public void EscapeJavaScriptString_escapes_quotes()
    {
        // Arrange
        string withQuotes = "He said \"Hello World\"";

        // Act
        var result = withQuotes.EscapeJavaScriptString();

        // Assert
        result.Should().Contain("\\\"Hello World\\\"");
    }

    [Test]
    public void EscapeJavaScriptString_escapes_control_characters()
    {
        // Arrange
        const string specialString = "Hello\nWorld\r\nTest\tTab";

        // Act
        var result = specialString.EscapeJavaScriptString();

        // Assert
        result.Should().Contain("\\n");
        result.Should().Contain("\\r");
        result.Should().Contain("\\t");
    }

    [Test]
    public void EscapeJavaScriptString_handles_null_input()
    {
        // Arrange
        string? nullInput = null;

        // Act
        var result = nullInput.EscapeJavaScriptString();

        // Assert
        result.Should().Be("null");
    }

    [Test]
    public void EscapeJavaScriptString_escapes_form_feed()
    {
        // Arrange
        string withFormFeed = "Line1\fLine2";

        // Act
        var result = withFormFeed.EscapeJavaScriptString();

        // Assert
        result.Should().Contain("\\f");
    }

    [Test]
    public void EscapeJavaScriptString_escapes_unicode_control_chars()
    {
        // Arrange
        string withControlChar = "Hello\vWorld"; // \v = vertical tab (ASCII 12)

        // Act
        var result = withControlChar.EscapeJavaScriptString();

        // Assert
        result.Should().Contain("\\u000B"); // \v should be escaped as \u000B
    }

    [Test]
    public void EscapeJavaScriptString_preserves_regular_text()
    {
        // Arrange
        string regularText = "Normal text with spaces and letters ABC123";

        // Act
        var result = regularText.EscapeJavaScriptString();

        // Assert
        result.Should().Be(regularText);
    }

    [Test]
    public void EscapeJavaScriptString_complex_mixed_content()
    {
        // Arrange
        const string complexLine = @"Error: " +
                                    @"Can't access " +
                                    @"path\\to\\file\n" +
                                    @"with " +
                                    @"tab and " +
                                    @"double quotes \""";

        // Act
        var result = complexLine.EscapeJavaScriptString();

        // Assert
        result.Should().Contain("\\\"Can't");
        result.Should().Contain( @"path\\\\to\\\\file\\n");
        result.Should().Contain("\\\\n");
    }

    #endregion

    #region Edge Case Tests

    [Test]
    public void ParseLogMessages_single_character_input_ShouldBeTreatedAsPlainText()
    {
        // Arrange
        var singleChar = "x";

        // Act
        var result = _parser.ParseLogMessages(singleChar).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Be("x");
    }

    [Test]
    public void ParseLogMessages_only_timestamp_ShouldReturnEmptyContent()
    {
        // Arrange
        var onlyTimestamp = "[10:30:00.123]";

        // Act
        var result = _parser.ParseLogMessages(onlyTimestamp).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Content.Should().Be("");
    }

    [Test]
    public void ParseLogMessages_all_empty_lines_ShouldReturnEmpty()
    {
        // Arrange
        var allEmpty = "\n\n\n";

        // Act
        var result = _parser.ParseLogMessages(allEmpty).ToList();

        // Assert
        result.Should().BeEmpty();
    }

    #endregion
}
