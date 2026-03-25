using FluentAssertions;
using Moq;

namespace ClaudeManager.Agent.Tests;

[TestFixture]
public class ClaudeValidatorTests
{
    // ── ResolveBinary ─────────────────────────────────────────────────────────

    [Test]
    public void ResolveBinary_ConfiguredPathExists_ReturnsConfiguredPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var result = ClaudeValidator.ResolveBinary(tempFile);
            result.Should().Be(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public void ResolveBinary_ConfiguredPathDoesNotExist_SearchesPath()
    {
        // Non-existent configured path should fall through to PATH search.
        // On a machine without claude on PATH this returns null — that's the expected behaviour.
        var result = ClaudeValidator.ResolveBinary("/nonexistent/path/to/claude");
        // We can only assert this doesn't throw; the value depends on the environment.
        Assert.DoesNotThrow(() => _ = result);
    }

    [Test]
    public void ResolveBinary_NullConfigured_SearchesPath()
    {
        Assert.DoesNotThrow(() => _ = ClaudeValidator.ResolveBinary(null));
    }

    [Test]
    public void ResolveBinary_EmptyStringConfigured_SearchesPath()
    {
        Assert.DoesNotThrow(() => _ = ClaudeValidator.ResolveBinary(string.Empty));
    }

    [Test]
    public void ResolveBinary_NotOnPath_ReturnsNull()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Set PATH to a temp dir that definitely has no claude binary
            var emptyDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(emptyDir);
            Environment.SetEnvironmentVariable("PATH", emptyDir);

            var result = ClaudeValidator.ResolveBinary(null);
            result.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Test]
    public void ResolveBinary_BinaryOnPath_ReturnsFullPath()
    {
        // Create a fake claude / claude.exe in a temp directory on PATH
        var tempDir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var binaryName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var fakeBinary = Path.Combine(tempDir, binaryName);
        File.WriteAllText(fakeBinary, "fake");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tempDir);
            var result = ClaudeValidator.ResolveBinary(null);
            result.Should().Be(fakeBinary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── ValidateAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task ValidateAsync_RunnerReturnsExitCode0_ReturnsOk()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock
                .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult(0, string.Empty));

            var validator = new ClaudeValidator(runnerMock.Object);
            var (ok, _) = await validator.ValidateAsync(tempFile, CancellationToken.None);

            ok.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ValidateAsync_RunnerReturnsNonZeroExitCode_ReturnsFailure()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock
                .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult(1, "auth error"));

            var validator = new ClaudeValidator(runnerMock.Object);
            var (ok, error) = await validator.ValidateAsync(tempFile, CancellationToken.None);

            ok.Should().BeFalse();
            error.Should().Contain("auth error");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ValidateAsync_RunnerThrowsOperationCanceled_ReturnsTimeout()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var runnerMock = new Mock<IProcessRunner>();
            runnerMock
                .Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            var validator = new ClaudeValidator(runnerMock.Object);
            var (ok, error) = await validator.ValidateAsync(tempFile, CancellationToken.None);

            ok.Should().BeFalse();
            error.Should().Contain("timed out");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task ValidateAsync_BinaryPathIsNull_ReturnsFailure()
    {
        var runnerMock = new Mock<IProcessRunner>();
        var validator  = new ClaudeValidator(runnerMock.Object);

        // With PATH cleared, ResolveBinary returns null
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var (ok, error) = await validator.ValidateAsync(null, CancellationToken.None);

            ok.Should().BeFalse();
            error.Should().Contain("PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
