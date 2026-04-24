using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Tests for <see cref="SweAfProvisioningService.BuildWriteFileCommand"/>.
/// These are pure static-method tests — no DB or services needed.
/// </summary>
[TestFixture]
public class SweAfProvisioningOpencodeTests
{
    private const string _repoPath = "/home/user/swe-af";

    private static string DecodeFileWriteCommand(string wrapped, out string renderedContent)
    {
        var echoPrefix = "echo ";
        var pipeSuffix = " | base64 -d > ";
        wrapped.Should().StartWith(echoPrefix);
        var b64Start = wrapped[echoPrefix.Length..].IndexOf('>') + 1;
        // Actually, the command is: echo {b64} | base64 -d > {path}
        // We need to find the b64 portion between "echo " and " | base64 -d > "
        var pipeIdx = wrapped.IndexOf(" | base64 -d > ");
        pipeIdx.Should().NotBe(-1);
        var b64 = wrapped[echoPrefix.Length..pipeIdx].Trim();
        renderedContent = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        return wrapped;
    }

    [Test]
    public void ReturnsWrappedBase64Command()
    {
        var content = "{\"baseURL\":\"http://test:12345/v1\"}";
        var cmd = SweAfProvisioningService.BuildWriteFileCommand(content, _repoPath);
        cmd.Should().StartWith("echo ");
        cmd.Should().Contain(" | base64 -d > ");
        cmd.Should().EndWith(_repoPath);
    }

    [Test]
    public void DecodedContent_MatchesOriginalContent()
    {
        var content = "{\"baseURL\":\"http://test:12345/v1\"}";
        DecodeFileWriteCommand(SweAfProvisioningService.BuildWriteFileCommand(content, _repoPath), out var decoded);
        decoded.Should().Be(content);
    }

    [Test]
    public void FileWriteCommand_WritesToCorrectPath()
    {
        var content = "{}";
        var cmd = SweAfProvisioningService.BuildWriteFileCommand(content, _repoPath);
        cmd.Should().EndWith(_repoPath);
    }

    [Test]
    public void Base64EncodedContent_ValidJson()
    {
        var content = "{\n  \"$schema\": \"https://opencode.ai/config.json\",\n  \"provider\": \"test\"\n}";
        DecodeFileWriteCommand(SweAfProvisioningService.BuildWriteFileCommand(content, _repoPath), out var decoded);
        System.Text.Json.JsonDocument.Parse(decoded);
    }
}
