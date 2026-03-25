using FluentAssertions;

namespace ClaudeManager.Agent.Tests;

/// <summary>
/// Tests for MachineIdProvider.GetOrCreate.
/// NonParallelizable to prevent races on PATH-modifying tests — though
/// these tests use temp paths, so parallelisation would be safe here too.
/// </summary>
[TestFixture]
[NonParallelizable]
public class MachineIdProviderTests
{
    private string _tempDir = default!;
    private string _tempFile = default!;

    [SetUp]
    public void SetUp()
    {
        _tempDir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFile = Path.Combine(_tempDir, "machine_id");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void GetOrCreate_NoExistingFile_WritesNewGuidToFile()
    {
        MachineIdProvider.GetOrCreate(_tempFile);

        File.Exists(_tempFile).Should().BeTrue();
        var content = File.ReadAllText(_tempFile).Trim();
        Guid.TryParse(content, out _).Should().BeTrue();
    }

    [Test]
    public void GetOrCreate_NoExistingFile_ReturnsValidGuid()
    {
        var result = MachineIdProvider.GetOrCreate(_tempFile);

        Guid.TryParse(result, out _).Should().BeTrue();
    }

    [Test]
    public void GetOrCreate_ExistingValidGuidFile_ReturnsSameGuid()
    {
        var expected = Guid.NewGuid().ToString();
        File.WriteAllText(_tempFile, expected);

        var result = MachineIdProvider.GetOrCreate(_tempFile);

        result.Should().Be(expected);
    }

    [Test]
    public void GetOrCreate_ExistingValidGuidFile_DoesNotWriteNewValue()
    {
        var expected = Guid.NewGuid().ToString();
        File.WriteAllText(_tempFile, expected);

        MachineIdProvider.GetOrCreate(_tempFile);

        File.ReadAllText(_tempFile).Trim().Should().Be(expected);
    }

    [Test]
    public void GetOrCreate_ExistingFileWithInvalidContent_GeneratesNewGuid()
    {
        File.WriteAllText(_tempFile, "not-a-guid");

        var result = MachineIdProvider.GetOrCreate(_tempFile);

        Guid.TryParse(result, out _).Should().BeTrue();
    }

    [Test]
    public void GetOrCreate_ExistingFileWithInvalidContent_OverwritesFile()
    {
        File.WriteAllText(_tempFile, "not-a-guid");

        MachineIdProvider.GetOrCreate(_tempFile);

        var stored = File.ReadAllText(_tempFile).Trim();
        Guid.TryParse(stored, out _).Should().BeTrue();
    }
}
