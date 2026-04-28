using ClaudeManager.Hub.Services.Docker;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class DockerCommandBuilderTests
{
    // ── Build validation ──────────────────────────────────────────────────────

    [Test]
    public void Build_NoImageAndDefaultName_Throws()
    {
        var builder = new DockerCommandBuilder();
        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Build_ImageSet_DoesNotThrow()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx:latest")
            .Build();
        cmd.Should().NotBeNull();
    }

    [Test]
    public void Build_CustomNameNoImage_DoesNotThrow()
    {
        var cmd = new DockerCommandBuilder()
            .WithContainerName("my-container")
            .Build();
        cmd.Should().NotBeNull();
    }

    // ── Command prefix ────────────────────────────────────────────────────────

    [Test]
    public void Build_ArgsStartWithRun()
    {
        var cmd = new DockerCommandBuilder().WithImage("nginx").Build();
        cmd.Args.Should().StartWith("run ");
    }

    // ── Detached / interactive mode ───────────────────────────────────────────

    [Test]
    public void InDetachedMode_AddsFlag_d()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .InDetachedMode()
            .Build();
        cmd.Args.Should().Contain("-d");
        cmd.Args.Should().NotContain("-itd");
    }

    [Test]
    public void InInteractiveMode_NoDetachFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .InInteractiveMode()
            .Build();
        cmd.Args.Should().NotContain("-d");
        cmd.Args.Should().NotContain("-itd");
    }

    [Test]
    public void WithInteractiveDetached_AddsFlag_itd()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithInteractiveDetached()
            .Build();
        cmd.Args.Should().Contain("-itd");
        cmd.Args.Should().NotContain(" -d ");
    }

    [Test]
    public void DefaultMode_IsDetached()
    {
        var cmd = new DockerCommandBuilder().WithImage("nginx").Build();
        cmd.Args.Should().Contain("-d");
    }

    // ── Container name ────────────────────────────────────────────────────────

    [Test]
    public void WithContainerName_AppearsInArgs()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithContainerName("my-nginx")
            .Build();
        cmd.Args.Should().Contain("--name my-nginx");
    }

    // ── Image placement ───────────────────────────────────────────────────────

    [Test]
    public void WithImage_AppearsAfterFlagsBeforeCommandArgs()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx:1.25")
            .WithCommand("--some-arg")
            .Build();

        var imageIdx   = cmd.Args.IndexOf("nginx:1.25", StringComparison.Ordinal);
        var cmdArgIdx  = cmd.Args.IndexOf("--some-arg", StringComparison.Ordinal);
        imageIdx.Should().BeLessThan(cmdArgIdx);
    }

    // ── Port mapping ──────────────────────────────────────────────────────────

    [Test]
    public void WithPortMapping_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithPortMapping(8080, "80")
            .Build();
        cmd.Args.Should().Contain("-p 8080:80");
    }

    [Test]
    public void WithPortMapping_DefaultContainerPort()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithPortMapping(9000)
            .Build();
        cmd.Args.Should().Contain("-p 9000:8000");
    }

    // ── Environment variables ─────────────────────────────────────────────────

    [Test]
    public void WithEnvironmentVariable_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithEnvironmentVariable("FOO", "bar")
            .Build();
        cmd.Args.Should().Contain("-e FOO=bar");
    }

    [Test]
    public void WithEnvironmentVariables_AddsDictionary()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithEnvironmentVariables(new Dictionary<string, string>
            {
                ["KEY1"] = "val1",
                ["KEY2"] = "val2",
            })
            .Build();
        cmd.Args.Should().Contain("-e KEY1=val1");
        cmd.Args.Should().Contain("-e KEY2=val2");
    }

    [Test]
    public void MultipleEnvVars_AllPresent()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithEnvironmentVariable("A", "1")
            .WithEnvironmentVariable("B", "2")
            .WithEnvironmentVariable("C", "3")
            .Build();
        cmd.Args.Should().Contain("-e A=1");
        cmd.Args.Should().Contain("-e B=2");
        cmd.Args.Should().Contain("-e C=3");
    }

    // ── Volumes ───────────────────────────────────────────────────────────────

    [Test]
    public void WithVolume_NoMode_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithVolume("/host/path", "/container/path")
            .Build();
        cmd.Args.Should().Contain("-v /host/path:/container/path");
    }

    [Test]
    public void WithVolume_ReadOnly_AddsMode()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithVolume("/host/path", "/container/path", "ro")
            .Build();
        cmd.Args.Should().Contain("-v /host/path:/container/path:ro");
    }

    [Test]
    public void WithVolume_ReadWrite_AddsMode()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithVolume("/data", "/data", "rw")
            .Build();
        cmd.Args.Should().Contain("-v /data:/data:rw");
    }

    [Test]
    public void MultipleVolumes_AllPresent()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithVolume("/a", "/a")
            .WithVolume("/b", "/b", "ro")
            .Build();
        cmd.Args.Should().Contain("-v /a:/a");
        cmd.Args.Should().Contain("-v /b:/b:ro");
    }

    // ── Devices ───────────────────────────────────────────────────────────────

    [Test]
    public void WithDevice_NoContainerPath_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nvidia/cuda")
            .WithDevice("/dev/nvidia0")
            .Build();
        cmd.Args.Should().Contain("--device /dev/nvidia0");
    }

    [Test]
    public void WithDevice_WithContainerPath_AddsMapping()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nvidia/cuda")
            .WithDevice("/dev/nvidia0", "/dev/nvidia0")
            .Build();
        cmd.Args.Should().Contain("--device /dev/nvidia0:/dev/nvidia0");
    }

    // ── GPU / NVIDIA ──────────────────────────────────────────────────────────

    [Test]
    public void WithGpus_AddsQuotedDeviceSpec()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nvidia/cuda")
            .WithGpus("device=0,1")
            .Build();
        cmd.Args.Should().Contain("--gpus '\"device=0,1\"'");
    }

    [Test]
    public void WithGpus_All_AddsAllSpec()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nvidia/cuda")
            .WithGpus("all")
            .Build();
        cmd.Args.Should().Contain("--gpus '\"all\"'");
    }

    [Test]
    public void WithNvidiaRuntime_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nvidia/cuda")
            .WithNvidiaRuntime()
            .Build();
        cmd.Args.Should().Contain("--runtime nvidia");
    }

    // ── Networking / IPC ──────────────────────────────────────────────────────

    [Test]
    public void WithHostNetwork_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithHostNetwork()
            .Build();
        cmd.Args.Should().Contain("--network host");
    }

    [Test]
    public void WithHostIPC_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithHostIPC()
            .Build();
        cmd.Args.Should().Contain("--ipc=host");
    }

    // ── Shared memory ─────────────────────────────────────────────────────────

    [Test]
    public void WithShmSize_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithShmSize("16G")
            .Build();
        cmd.Args.Should().Contain("--shm-size 16G");
    }

    // ── Restart policy ────────────────────────────────────────────────────────

    [Test]
    public void WithRestartPolicy_Detached_NotAdded()
    {
        // Restart policy is silently dropped when detached — document this behavior
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .InDetachedMode()
            .WithRestartPolicy("unless-stopped")
            .Build();
        cmd.Args.Should().NotContain("--restart");
    }

    [Test]
    public void WithRestartPolicy_NonDetached_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .InInteractiveMode()
            .WithRestartPolicy("unless-stopped")
            .Build();
        cmd.Args.Should().Contain("--restart unless-stopped");
    }

    [Test]
    public void WithRestartPolicy_NonDetached_DefaultNo_NotAdded()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .InInteractiveMode()
            .Build();
        cmd.Args.Should().NotContain("--restart");
    }

    // ── Entrypoint ────────────────────────────────────────────────────────────

    [Test]
    public void WithEntrypoint_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithEntrypoint("/bin/bash")
            .Build();
        cmd.Args.Should().Contain("--entrypoint /bin/bash");
    }

    [Test]
    public void WithoutEntrypoint_NoFlag()
    {
        var cmd = new DockerCommandBuilder().WithImage("nginx").Build();
        cmd.Args.Should().NotContain("--entrypoint");
    }

    // ── Command args ──────────────────────────────────────────────────────────

    [Test]
    public void WithCommand_AppearsAtEnd()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("python:3.12")
            .WithCommand("python", "app.py", "--port=8080")
            .Build();

        var imageIdx = cmd.Args.IndexOf("python:3.12", StringComparison.Ordinal);
        var argIdx   = cmd.Args.IndexOf("python app.py --port=8080", StringComparison.Ordinal);
        argIdx.Should().BeGreaterThan(imageIdx);
        cmd.Args.Should().EndWith("python app.py --port=8080");
    }

    // ── Working directory ─────────────────────────────────────────────────────

    [Test]
    public void WithWorkingDirectory_AddsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithWorkingDirectory("/app")
            .Build();
        cmd.Args.Should().Contain("-w /app");
    }

    // ── Custom flag ───────────────────────────────────────────────────────────

    [Test]
    public void WithFlag_AddsArbitraryFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithFlag("--privileged")
            .Build();
        cmd.Args.Should().Contain("--privileged");
    }

    // ── Sudo ──────────────────────────────────────────────────────────────────

    [Test]
    public void RequiresSudo_SetsFlag()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .RequiresSudo()
            .Build();
        cmd.RequiresSudo.Should().BeTrue();
    }

    [Test]
    public void RequiresSudo_WithPassword_SetsBoth()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .RequiresSudo("mypassword")
            .Build();
        cmd.RequiresSudo.Should().BeTrue();
        cmd.SudoPassword.Should().Be("mypassword");
    }

    [Test]
    public void WithoutSudo_DefaultsFalse()
    {
        var cmd = new DockerCommandBuilder().WithImage("nginx").Build();
        cmd.RequiresSudo.Should().BeFalse();
        cmd.SudoPassword.Should().BeNull();
    }

    // ── Argument ordering ─────────────────────────────────────────────────────

    [Test]
    public void Build_OrderIs_Run_Mode_Name_Flags_Env_Volumes_Image_Cmd()
    {
        var cmd = new DockerCommandBuilder()
            .WithImage("myimage:latest")
            .WithContainerName("mycontainer")
            .WithEnvironmentVariable("ENV", "val")
            .WithVolume("/h", "/c")
            .WithPortMapping(1234, "5678")
            .WithCommand("serve")
            .Build();

        var args = cmd.Args;
        var runIdx    = args.IndexOf("run",              StringComparison.Ordinal);
        var modeIdx   = args.IndexOf("-d",               StringComparison.Ordinal);
        var nameIdx   = args.IndexOf("--name mycontainer", StringComparison.Ordinal);
        var portIdx   = args.IndexOf("-p 1234:5678",     StringComparison.Ordinal);
        var envIdx    = args.IndexOf("-e ENV=val",       StringComparison.Ordinal);
        var volIdx    = args.IndexOf("-v /h:/c",         StringComparison.Ordinal);
        var imageIdx  = args.IndexOf("myimage:latest",   StringComparison.Ordinal);
        var cmdIdx    = args.IndexOf("serve",            StringComparison.Ordinal);

        runIdx.Should().BeLessThan(modeIdx);
        modeIdx.Should().BeLessThan(nameIdx);
        nameIdx.Should().BeLessThan(portIdx);
        portIdx.Should().BeLessThan(envIdx);
        envIdx.Should().BeLessThan(volIdx);
        volIdx.Should().BeLessThan(imageIdx);
        imageIdx.Should().BeLessThan(cmdIdx);
    }

    // ── Fluent chaining ───────────────────────────────────────────────────────

    [Test]
    public void FluentChain_ReturnsBuilderEachStep()
    {
        // Compile-time check that all methods return DockerCommandBuilder
        var cmd = new DockerCommandBuilder()
            .WithImage("nginx")
            .WithContainerName("test")
            .InDetachedMode()
            .WithPortMapping(80, "80")
            .WithEnvironmentVariable("X", "y")
            .WithVolume("/a", "/b")
            .WithShmSize("8G")
            .WithHostNetwork()
            .WithNvidiaRuntime()
            .WithGpus("all")
            .WithHostIPC()
            .WithFlag("--privileged")
            .RequiresSudo("pass")
            .Build();

        cmd.Args.Should().NotBeNullOrEmpty();
    }
}
