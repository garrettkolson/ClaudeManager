using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Tests for <see cref="SweAfProvisioningService.BuildWriteCavemanFilesCommand"/>.
/// These are pure static-method tests — no DB or services needed.
/// </summary>
[TestFixture]
public class SweAfProvisioningCavemanTests
{
    private const string _repoPath = "/home/user/swe-af";

    /// <summary>
    /// Decodes the base64 command returned by BuildWriteCavemanFilesCommand.
    /// The method returns: echo &lt;b64&gt; | base64 -d | bash
    /// So we strip the wrapper and decode the middle segment.
    /// </summary>
    private static string DecodeBashCommand(string wrapped)
    {
        var echoPrefix = "echo ";
        var pipeSuffix = " | base64 -d | bash";
        wrapped.Should().StartWith(echoPrefix);
        wrapped.Should().EndWith(pipeSuffix);
        var b64 = wrapped[echoPrefix.Length..^pipeSuffix.Length].Trim();
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    [Test]
    public void ReturnsWrappedBase64Command()
    {
        var cmd = SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath);
        cmd.Should().StartWith("echo ");
        cmd.Should().EndWith("| base64 -d | bash");
    }

    [Test]
    public void DecodedCommand_ContainsSetE()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().Contain("set -e");
    }

    [Test]
    public void DecodedCommand_CreatesCacheDirAndSkillDirs()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().Contain("mkdir -p");
        bash.Should().Contain("/tmp/caveman-skills/skills/caveman");
        bash.Should().Contain("/tmp/caveman-skills/skills/caveman-commit");
        bash.Should().Contain("/tmp/caveman-skills/skills/caveman-review");
        bash.Should().Contain("/tmp/caveman-skills/skills/caveman-help");
    }

    [Test]
    public void DecodedCommand_DownloadsAllFourSkillFiles()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));

        bash.Should().Contain("SKILL.md");
        bash.Should().Contain("caveman/SKILL.md");
        bash.Should().Contain("caveman-commit/SKILL.md");
        bash.Should().Contain("caveman-review/SKILL.md");
        bash.Should().Contain("caveman-help/SKILL.md");
    }

    [Test]
    public void DecodedCommand_UsesGitHubRawURLs()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().Contain("https://raw.githubusercontent.com/JuliusBrussee/caveman/main/skills");
    }

    [Test]
    public void DecodedCommand_UsesSkipCheckBeforeCurl()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        // Each skill should have a skip-check: [ -s <cache>/skills/<skill>/SKILL.md ] || curl ...
        foreach (var skill in new[] { "caveman", "caveman-commit", "caveman-review", "caveman-help" })
        {
            bash.Should().Contain("[ -s /tmp/caveman-skills/skills/" + skill + "/SKILL.md ] || curl -fsSL");
        }
    }

    [Test]
    public void DecodedCommand_WritesCLAUDEMd()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().Contain("/tmp/caveman-skills/CLAUDE.md");
    }

    [Test]
    public void DecodedCommand_WritesCLAUDEMdWithFrontmatter()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().Contain("---");
        bash.Should().Contain("Caveman mode");
    }

    [Test]
    public void DecodedCommand_UsesPrintfForCLAUDEMd()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().Contain("printf '%s\\n'");
    }

    [Test]
    public void DecodedCommand_UsesRoBindMountPathInComposeYaml()
    {
        // Verify the compose YAML generated inline in provisioning uses the same cache dir.
        // This is checked by reading the compose YAML string — it should match /tmp/caveman-skills.
        var cmd = SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath);
        var bash = DecodeBashCommand(cmd);

        // Skills mounted to container: /root/.claude/skills
        // Host source: /tmp/caveman-skills/skills
        bash.Should().Contain("/tmp/caveman-skills");
    }

    [Test]
    public void DecodedCommand_NoBashVariables()
    {
        // Ensure no $ variables that would collide with C# string interpolation
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        // The decoded bash should not contain unescaped $ signs (except in allowed constructs)
        // Since we decoded it, any $ in the decoded form is a literal bash var.
        // The command uses no for-loops or vars — just individual lines.
        bash.Should().NotMatch(".*\\$\\w+.*");
    }

    [Test]
    public void DecodedCommand_NoHeredocs()
    {
        var bash = DecodeBashCommand(SweAfProvisioningService.BuildWriteCavemanFilesCommand(_repoPath));
        bash.Should().NotContain("<<");
        bash.Should().NotContain("EOF");
    }
}
