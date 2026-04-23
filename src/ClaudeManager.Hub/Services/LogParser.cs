using System.Globalization;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Interface for parsing log messages from raw log strings.
/// </summary>
public interface ILogParser
{
    /// <summary>
    /// Parses raw log string into structured LogMessage instances.
    /// Handles common log formats:
    /// - [HH:mm:ss.fff] Message
    /// - [HH:mm:ss.ffffff] Message
    /// - Plain text messages without timestamp
    /// </summary>
    /// <param name="rawLogs">Raw log string possibly containing timestamp prefixes.</param>
    /// <returns>IEnumerable of parsed log messages with their line numbers.</returns>
    IEnumerable<LogMessage> ParseLogMessages(string rawLogs);
}

/// <summary>
/// Record to represent a parsed log message with timestamp, content, and line number.
/// </summary>
public record LogMessage
{
    /// <summary>Log entry timestamp parsed from log prefix format [HH:mm:ss.fff].</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Raw log content without timestamp prefix.</summary>
    public string Content { get; init; }

    /// <summary>Original line number in source log file (for future line-by-line features).</summary>
    public int LineNumber { get; init; }
}

/// <summary>
/// Service that parses log messages from raw log strings.
///
/// This service handles:
/// - Timestamp parsing from format [HH:mm:ss.fff] or [HH:mm:ss.ffffff]
/// - Plain text messages without timestamp
/// - Filtering empty/whitespace-only lines
/// </summary>
public class LogParser : ILogParser
{
    /// <summary>
    /// Parses raw log string into structured LogMessage instances.
    ///
    /// This method handles both timestamped logs ([HH:mm:ss.fff] Message) and plain text logs.
    /// Lines with timestamps are split into separate timestamp and content display.
    /// Empty or whitespace-only input returns an empty collection.
    /// </summary>
    /// <param name="rawLogs">Raw log string possibly containing timestamp prefixes.</param>
    /// <returns>IEnumerable of parsed log messages with their line numbers.</returns>
    public virtual IEnumerable<LogMessage> ParseLogMessages(string rawLogs)
    {
        if (string.IsNullOrWhiteSpace(rawLogs))
        {
            yield break;
        }

        const string timestampPattern = @"(\[\d{1,2}:\d{2}:\d{2}\.?(\d{3}|\d{6})\]) ";
        const string shortTimestampPattern = @"(\[\d{1,2}:\d{2}:\d{2}\.\d{3}\]) ";

        var lines = rawLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber];
            line = line.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Try to parse timestamp first (handles [HH:mm:ss.fff] format)
            if (line.StartsWith('[') && line.Contains(']'))
            {
                var bracketEndIndex = line.IndexOf(']');
                var timestampText = line.Substring(1, bracketEndIndex - 1);

                if (TryParseDatetimeOffset(timestampText, out var timestamp))
                {
                    string content = bracketEndIndex + 2 < line.Length
                        ? line.Substring(bracketEndIndex + 2).Trim()
                        : "";
                    yield return new LogMessage
                    {
                        Timestamp = timestamp,
                        Content = content,
                        LineNumber = lineNumber + 1
                    };
                    continue;
                }
            }

            // Plain text log - treat entire line as content
            yield return new LogMessage
            {
                Timestamp = DateTimeOffset.UtcNow,
                Content = line,
                LineNumber = lineNumber + 1
            };
        }
    }

    /// <summary>
    /// Tries to parse a datetime offset from the timestamp text.
    ///
    /// Handles formats:
    /// - [HH:mm:ss.fff] (milliseconds precision)
    /// - [HH:mm:ss.ffffff] (microseconds precision)
    ///
    /// Returns true if parsing succeeds, false otherwise.
    /// </summary>
    /// <param name="timestampText">The timestamp text without brackets (e.g., "10:30:00.123").</param>
    /// <param name="result">The parsed DateTimeOffset if successful.</param>
    /// <returns>True if parsing was successful, false otherwise.</returns>
    private static bool TryParseDatetimeOffset(string timestampText, out DateTimeOffset result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(timestampText) || timestampText.Length < 8)
        {
            return false;
        }

        // Format: HH:mm:ss.fff or HH:mm:ss.ffffff
        // Split on ':' first: "10" "30" "00.123"
        var parts = timestampText.Split(':');
        if (parts.Length < 3) return false;

        if (!int.TryParse(parts[0], out var hh)) return false;
        if (!int.TryParse(parts[1], out var mm)) return false;

        // seconds must include fractional part: "00.123" or "00.123456"
        var secPart = parts[2];
        var dotIdx = secPart.IndexOf('.');
        if (dotIdx < 0 || dotIdx >= secPart.Length - 1) return false;

        var secStr = secPart.Substring(0, dotIdx);
        var fracStr = secPart.Substring(dotIdx + 1);
        if (!int.TryParse(secStr, out var ss)) return false;
        if (fracStr.Length == 0 || fracStr.Length > 6) return false;
        if (!int.TryParse(fracStr, out var frac)) return false;

        // Normalize to milliseconds
        string msStr = fracStr.PadRight(3, '0').Substring(0, 3);
        if (!int.TryParse(msStr, out _)) return false;

        try
        {
            var today = DateTimeOffset.UtcNow;
            var dt = new DateTime(today.Year, today.Month, today.Day, hh, mm, ss, frac / 1000, DateTimeKind.Utc);
            result = new DateTimeOffset(dt, TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Extension method to escape special characters in string for use in JavaScript contexts.
///
/// This method is required for the CopyLogsToClipboard and DownloadLogs methods which
/// generate JavaScript code dynamically to perform browser clipboard operations.
///
/// Escapes the following characters:
/// - Backslash (\) -> \\
/// - Double quote (") -> \"
/// - Single quote (') -> \'
/// - Newline (\n) -> \n
/// - Carriage return (\r) -> \r
/// - Tab (\t) -> \t
/// - Backspace (\b) -> \b
/// - Form feed (\f) -> \f
/// - Control characters (< 32) -> \uXXXX
/// </summary>
/// <param name="input">The string to escape.</param>
/// <returns>String with special characters escaped for JavaScript.</returns>
public static class StringExtensions
{
    /// <summary>
    /// Escapes special characters in a string for use in JavaScript string literals.
    /// </summary>
    /// <param name="input">The string to escape.</param>
    /// <returns>String with special characters escaped for JavaScript.</returns>
    public static string EscapeJavaScriptString(this string input)
    {
        if (input is null)
        {
            return "null";
        }

        var result = new System.Text.StringBuilder();

        foreach (var c in input)
        {
            result.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                char c2 when c2 < 32 => $"\\u{(int)c2:X4}",
                _ => new string([c])
            });
        }

        return result.ToString();
    }
}
