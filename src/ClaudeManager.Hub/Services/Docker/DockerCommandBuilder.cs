namespace ClaudeManager.Hub.Services.Docker;

/// <summary>
/// Builder for constructing Docker commands with fluent API.
/// Implements the builder pattern to reduce code duplication across services.
/// </summary>
public class DockerCommandBuilder
{
    private const string DefaultContainerName = "default-container";
    private const int DefaultHostPort = 8000;

    private readonly List<string> _flags = [];
    private readonly List<string> _envVars = [];
    private readonly List<string> _volumes = [];
    private readonly List<string> _devices = [];
    private string _containerName = DefaultContainerName;
    private string _image = string.Empty;
    private string _entrypoint = string.Empty;
    private int _hostPort = DefaultHostPort;
    private string _containerPort = "8000";
    private string _restartPolicy = "no";
    private bool _detached = true;
    private bool _interactive = false;
    private string[] _commandArgs = [];
    private bool _requiresSudo = false;
    private string? _sudoPassword = null;

    /// <summary>
    /// Sets the container name.
    /// </summary>
    public DockerCommandBuilder WithContainerName(string name)
    {
        _containerName = name;
        return this;
    }

    /// <summary>
    /// Sets the Docker image to use.
    /// </summary>
    public DockerCommandBuilder WithImage(string image)
    {
        _image = image;
        return this;
    }

    /// <summary>
    /// Runs the container in detached mode (-d).
    /// </summary>
    public DockerCommandBuilder InDetachedMode()
    {
        _detached = true;
        return this;
    }

    /// <summary>
    /// Runs the container in interactive mode (-it).
    /// </summary>
    public DockerCommandBuilder InInteractiveMode()
    {
        _detached = false;
        return this;
    }

    /// <summary>
    /// Runs the container in interactive detached mode (-itd).
    /// Keeps stdin open and allocates a pseudo-TTY while running in the background.
    /// </summary>
    public DockerCommandBuilder WithInteractiveDetached()
    {
        _detached = true;
        _interactive = true;
        return this;
    }

    /// <summary>
    /// Sets the shared memory size (--shm-size), e.g. "16G".
    /// </summary>
    public DockerCommandBuilder WithShmSize(string size)
    {
        _flags.Add($"--shm-size {size}");
        return this;
    }

    /// <summary>
    /// Sets the restart policy (e.g., "unless-stopped", "always", "no").
    /// </summary>
    public DockerCommandBuilder WithRestartPolicy(string policy)
    {
        _restartPolicy = policy;
        return this;
    }

    /// <summary>
    /// Maps a host port to a container port (-p host:container).
    /// </summary>
    public DockerCommandBuilder WithPortMapping(int hostPort, string containerPort = "8000")
    {
        _hostPort = hostPort;
        _containerPort = containerPort;
        _flags.Add($"-p {hostPort}:{containerPort}");
        return this;
    }

    /// <summary>
    /// Uses host networking (--network host).
    /// </summary>
    public DockerCommandBuilder WithHostNetwork()
    {
        _flags.Add("--network host");
        return this;
    }

    /// <summary>
    /// Mounts a volume (-v hostPath:containerPath[:ro|rw]).
    /// </summary>
    public DockerCommandBuilder WithVolume(string hostPath, string containerPath, string? mode = null)
    {
        var volumeSpec = mode is not null ? $"{hostPath}:{containerPath}:{mode}" : $"{hostPath}:{containerPath}";
        _volumes.Add(volumeSpec);
        return this;
    }

    /// <summary>
    /// Sets an environment variable (-e KEY=VALUE).
    /// </summary>
    public DockerCommandBuilder WithEnvironmentVariable(string key, string value)
    {
        _envVars.Add($"{key}={value}");
        return this;
    }

    /// <summary>
    /// Sets multiple environment variables.
    /// </summary>
    public DockerCommandBuilder WithEnvironmentVariables(Dictionary<string, string> variables)
    {
        foreach (var kvp in variables)
        {
            _envVars.Add($"{kvp.Key}={kvp.Value}");
        }
        return this;
    }

    /// <summary>
    /// Allocates GPU devices (--gpus '"device=0,1"' or '--gpus all').
    /// </summary>
    public DockerCommandBuilder WithGpus(string deviceSpec)
    {
        _flags.Add($"--gpus '\"{deviceSpec}\"'");
        return this;
    }

    /// <summary>
    /// Uses the NVIDIA runtime (--runtime nvidia).
    /// </summary>
    public DockerCommandBuilder WithNvidiaRuntime()
    {
        _flags.Add("--runtime nvidia");
        return this;
    }

    /// <summary>
    /// Shares the host IPC namespace (--ipc=host).
    /// </summary>
    public DockerCommandBuilder WithHostIPC()
    {
        _flags.Add("--ipc=host");
        return this;
    }

    /// <summary>
    /// Adds a device mapping (--device).
    /// </summary>
    public DockerCommandBuilder WithDevice(string devicePath, string? containerPath = null)
    {
        var spec = containerPath is not null ? $"{devicePath}:{containerPath}" : devicePath;
        _devices.Add(spec);
        return this;
    }

    /// <summary>
    /// Sets the container entrypoint (--entrypoint).
    /// </summary>
    public DockerCommandBuilder WithEntrypoint(string entrypoint)
    {
        _entrypoint = entrypoint;
        return this;
    }

    /// <summary>
    /// Adds command arguments to be passed to the container.
    /// </summary>
    public DockerCommandBuilder WithCommand(params string[] args)
    {
        _commandArgs = args;
        return this;
    }

    /// <summary>
    /// Requires sudo privileges for execution.
    /// </summary>
    public DockerCommandBuilder RequiresSudo(string? sudoPassword = null)
    {
        _requiresSudo = true;
        _sudoPassword = sudoPassword;
        return this;
    }

    /// <summary>
    /// Sets a working directory (-w).
    /// </summary>
    public DockerCommandBuilder WithWorkingDirectory(string dir)
    {
        _flags.Add($"-w {dir}");
        return this;
    }

    /// <summary>
    /// Adds a custom flag.
    /// </summary>
    public DockerCommandBuilder WithFlag(string flag)
    {
        _flags.Add(flag);
        return this;
    }

    /// <summary>
    /// Builds the DockerCommand from the accumulated settings.
    /// </summary>
    public DockerCommand Build()
    {
        if (string.IsNullOrEmpty(_image) && _containerName == DefaultContainerName)
        {
            throw new InvalidOperationException("At least an image or container name must be specified.");
        }

        var args = new List<string> { "run" };

        if (_detached)
            args.Add(_interactive ? "-itd" : "-d");

        args.Add($"--name {_containerName}");

        if (!string.IsNullOrEmpty(_entrypoint))
            args.Add($"--entrypoint {_entrypoint}");

        args.AddRange(_flags);
        args.AddRange(_envVars.Select(e => $"-e {e}"));
        args.AddRange(_volumes.Select(v => $"-v {v}"));

        foreach (var device in _devices)
            args.Add($"--device {device}");

        if (!_detached && _restartPolicy != "no")
            args.Add($"--restart {_restartPolicy}");

        if (!string.IsNullOrEmpty(_image))
            args.Add(_image);

        args.AddRange(_commandArgs);

        return new DockerCommand(
            Args: string.Join(" ", args),//.Select(EscapeArg)),
            RequiresSudo: _requiresSudo,
            SudoPassword: _sudoPassword
        );
    }

    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"') || arg.Contains('\''))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }
        return arg;
    }
}
