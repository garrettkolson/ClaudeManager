using FluentAssertions;

namespace ClaudeManager.Agent.Tests;

[TestFixture]
public class ClaudeStreamJsonParserTests
{
    // ── ExtractSessionId ──────────────────────────────────────────────────────

    [Test]
    public void ExtractSessionId_ValidInitLine_ReturnsSessionId()
    {
        var json = "{\"type\":\"system\",\"session_id\":\"abc-123\",\"version\":\"1.0\"}";
        ClaudeStreamJsonParser.ExtractSessionId(json).Should().Be("abc-123");
    }

    [Test]
    public void ExtractSessionId_NoSessionIdMarker_ReturnsNull()
    {
        var json = "{\"type\":\"assistant\",\"content\":\"hello\"}";
        ClaudeStreamJsonParser.ExtractSessionId(json).Should().BeNull();
    }

    [Test]
    public void ExtractSessionId_SessionIdAtEndOfJson_ReturnsValue()
    {
        var json = "{\"type\":\"system\",\"session_id\":\"end-value\"}";
        ClaudeStreamJsonParser.ExtractSessionId(json).Should().Be("end-value");
    }

    [Test]
    public void ExtractSessionId_EmptyClosingQuote_ReturnsNull()
    {
        // Marker present but value is empty string — end == start, so returns null
        var json = "{\"session_id\":\"\"}";
        ClaudeStreamJsonParser.ExtractSessionId(json).Should().BeNull();
    }

    // ── IsResumeError ─────────────────────────────────────────────────────────

    [Test]
    public void IsResumeError_ErrorResultJson_ReturnsTrue()
    {
        var json = "{\"type\":\"result\",\"subtype\":\"error\",\"error\":\"session not found\"}";
        ClaudeStreamJsonParser.IsResumeError(json).Should().BeTrue();
    }

    [Test]
    public void IsResumeError_ResultTypeButNotErrorSubtype_ReturnsFalse()
    {
        var json = "{\"type\":\"result\",\"subtype\":\"success\"}";
        ClaudeStreamJsonParser.IsResumeError(json).Should().BeFalse();
    }

    [Test]
    public void IsResumeError_NoResultType_ReturnsFalse()
    {
        var json = "{\"type\":\"assistant\",\"subtype\":\"error\"}";
        ClaudeStreamJsonParser.IsResumeError(json).Should().BeFalse();
    }

    [Test]
    public void IsResumeError_EmptyString_ReturnsFalse()
    {
        ClaudeStreamJsonParser.IsResumeError(string.Empty).Should().BeFalse();
    }
}
