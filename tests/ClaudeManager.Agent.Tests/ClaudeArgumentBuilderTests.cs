using FluentAssertions;

namespace ClaudeManager.Agent.Tests;

[TestFixture]
public class ClaudeArgumentBuilderTests
{
    [Test]
    public void Build_NoResumeId_ReturnsExpectedFlags()
    {
        var result = ClaudeArgumentBuilder.Build("hello world", null);

        result.Should().Be("-p \"hello world\" --output-format stream-json");
    }

    [Test]
    public void Build_WithResumeId_IncludesResumeFlag()
    {
        var result = ClaudeArgumentBuilder.Build("continue", "sess-abc");

        result.Should().Contain("--resume sess-abc");
    }

    [Test]
    public void Build_PromptWithDoubleQuotes_EscapesQuotes()
    {
        var result = ClaudeArgumentBuilder.Build("say \"hello\"", null);

        result.Should().Contain("say \\\"hello\\\"");
    }

    [Test]
    public void Build_PromptWithBackslashes_EscapesBackslashes()
    {
        var result = ClaudeArgumentBuilder.Build("path\\to\\file", null);

        result.Should().Contain("path\\\\to\\\\file");
    }

    [Test]
    public void Build_PromptWithBothEscapableChars_EscapesBoth()
    {
        var result = ClaudeArgumentBuilder.Build("\"C:\\path\"", null);

        result.Should().Contain("\\\"C:\\\\path\\\"");
    }
}
