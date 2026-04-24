using System.Diagnostics;
using System.Linq;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

public interface ISwarmProvisioningService
{
    Task<(bool Success, string? Error, string? ControlPlaneUrl)>
        ProvisionControlPlaneAsync(CancellationToken ct = default);

    Task<(bool Success, string? Error, string? ControlPlaneUrl)>
        ProvisionControlPlaneForJobAsync(
            long jobId, int cpPort, int agentPort, int fastPort, CancellationToken ct = default);

    Task<string?> StopControlPlaneForJobAsync(
        string projectName, CancellationToken ct = default);

    Task<List<string>> ListActiveComposeProjectsAsync(CancellationToken ct = default);

    Task<(string? Logs, string? Error)> GetContainerLogsAsync(
        string projectName, int lines = 200, CancellationToken ct = default);
}

/// <summary>
/// Provisions (runs via Docker) an AgentField control plane on a host.
/// Uses Docker on localhost machines and SSH + Docker for remote machines.
/// </summary>
public class SweAfProvisioningService(
    IDbContextFactory<ClaudeManagerDbContext> dbFactory,
    ILogger<SweAfProvisioningService> logger) 
    : ISwarmProvisioningService
{
    private const string DefaultSweAfRepoPath = "~/swe-af";

    /// <summary>
    /// Runs the AgentField control plane using Docker container.
    /// Returns the control plane URL on success, or an error message on failure.
    /// </summary>
    public async Task<(bool Success, string? Error, string? ControlPlaneUrl)>
        ProvisionControlPlaneAsync(CancellationToken ct = default)
    {
        // TODO: convert all this to use the centralized Docker command logic
        var config = await GetConfigAsync(ct);

        if (!IsProvisioningConfigured(config))
        {
            return (false, "Provisioning host is not configured. Please configure SSH credentials and provisioning host first.", null);
        }

        var hostUrl = $"http://{config.ProvisionHost}:8080";

        var repoPath = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
            ? DefaultSweAfRepoPath
            : config.SweAfRepoPath;

        logger.LogInformation(
            "Writing .env and starting SWE-AF stack via docker compose on {Host} (repo: {Path})",
            config.ProvisionHost, repoPath);

        // Step 1: Write .env for docker compose
        var anthropicBaseUrl = await ResolveAnthropicBaseUrlAsync(config, ct);
        var writeEnvCmd = BuildWriteEnvCommand(config, repoPath, anthropicBaseUrl);
        var (envStdout, envStderr, envExitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(writeEnvCmd, ct)
            : await ExecSshShellAsync(config, writeEnvCmd, ct);

        if (envExitCode != 0)
        {
            var errorMsg = (envStderr ?? envStdout ?? "Failed to write .env").Trim();
            logger.LogWarning(".env write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, $"Failed to write agent .env: {errorMsg}", null);
        }

        // Step 2: Write docker-compose.override.yml if configured
        if (!string.IsNullOrWhiteSpace(config.ComposeOverride))
        {
            var writeOverrideCmd = BuildWriteOverrideCommand(config.ComposeOverride, repoPath);
            var (ovStdout, ovStderr, ovExitCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(writeOverrideCmd, ct)
                : await ExecSshShellAsync(config, writeOverrideCmd, ct);

            if (ovExitCode != 0)
            {
                var errorMsg = (ovStderr ?? ovStdout ?? "Failed to write docker-compose.override.yml").Trim();
                logger.LogWarning("Override write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
                return (false, $"Failed to write docker-compose.override.yml: {errorMsg}", null);
            }
        }

        // Step 2.6: Write opencode.json and bind mount it into both agent services
        var hasOpencodeGlobal = !string.IsNullOrWhiteSpace(config.OpencodeJsonTemplate);
        if (hasOpencodeGlobal)
        {
            await ApplyOpencodeJsonConfigAsync(config, anthropicBaseUrl ?? "", repoPath, ct);
        }

        // Step 2.7: Write Caveman skill files if enabled
        var hasCavemanGlobal = config.CavemanEnabled;
        if (hasCavemanGlobal)
        {
            var writeCavemanCmd = BuildWriteCavemanFilesCommand(repoPath);
            var (cavemanOut, cavemanErr, cavemanCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(writeCavemanCmd, ct)
                : await ExecSshShellAsync(config, writeCavemanCmd, ct);

            if (cavemanCode != 0)
            {
                var errorMsg = (cavemanErr ?? cavemanOut ?? "Failed to download Caveman files").Trim();
                logger.LogWarning("Caveman files write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
                return (false, $"Failed to write Caveman skill files: {errorMsg}", null);
            }

            var cacheDir = "/tmp/caveman-skills";
            var cavemanOverridePath = $"{repoPath}/docker-compose.caveman.yml";
            var cavemanYaml = $"""
                services:
                  swe-agent:
                    volumes:
                      - {cacheDir}/skills:/root/.claude/skills:ro
                      - {cacheDir}/CLAUDE.md:/root/.claude/CLAUDE.md:ro
                  swe-fast:
                    volumes:
                      - {cacheDir}/skills:/root/.claude/skills:ro
                      - {cacheDir}/CLAUDE.md:/root/.claude/CLAUDE.md:ro
                """;
            var cavemanB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cavemanYaml.Trim()));
            var writeCavemanComposeCmd = $"echo {cavemanB64} | base64 -d > {cavemanOverridePath}";
            var (cavComposeOut, cavComposeErr, cavComposeCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(writeCavemanComposeCmd, ct)
                : await ExecSshShellAsync(config, writeCavemanComposeCmd, ct);

            if (cavComposeCode != 0)
            {
                var errorMsg = (cavComposeErr ?? cavComposeOut ?? "Failed to write docker-compose.caveman.yml").Trim();
                logger.LogWarning("Caveman compose write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
                return (false, $"Failed to write Caveman compose override: {errorMsg}", null);
            }
        }

        // Step 3: Tear down any existing stack, then bring everything back up
        var downCmd = $"cd {repoPath} && docker compose down";
        var (downStdout, downStderr, _) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(downCmd, ct)
            : await ExecSshShellAsync(config, downCmd, ct);
        logger.LogInformation("docker compose down on {Host}: {Out}", config.ProvisionHost,
            (downStdout ?? downStderr ?? "").Trim());

        // docker-compose.override.yml is auto-loaded; -f chain is not needed for it.
        // docker-compose.caveman.yml and docker-compose.opencode.yml are NOT auto-loaded,
        // so they must be explicitly included.
        var composeFilesParts = new List<string> { "-f docker-compose.yml" };
        if (hasCavemanGlobal) composeFilesParts.Add("-f docker-compose.caveman.yml");
        if (hasOpencodeGlobal) composeFilesParts.Add("-f docker-compose.opencode.yml");
        var composeFiles = string.Join(" ", composeFilesParts);
        var composeCmd = string.IsNullOrEmpty(composeFiles)
            ? $"cd {repoPath} && docker compose up -d --build"
            : $"cd {repoPath} && docker compose {composeFiles} up -d --build";
        var (compStdout, compStderr, compExitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(composeCmd, ct)
            : await ExecSshShellAsync(config, composeCmd, ct);

        if (compExitCode != 0)
        {
            var errorMsg = (compStderr ?? compStdout ?? "docker compose failed").Trim();
            logger.LogWarning("docker compose failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, $"docker compose up failed: {errorMsg}", null);
        }

        logger.LogInformation(
            "SWE-AF stack started successfully on {Host} -> {Url}",
            config.ProvisionHost, hostUrl);

        return (true, null, hostUrl);
    }

    // ── Per-build provisioning ────────────────────────────────────────────────

    /// <summary>
    /// Provisions an isolated AgentField control plane for a single build job.
    /// Uses a unique Docker Compose project name so the container is fully isolated.
    /// Returns the control plane URL on success, or an error message on failure.
    /// </summary>
    public async Task<(bool Success, string? Error, string? ControlPlaneUrl)>
        ProvisionControlPlaneForJobAsync(
            long jobId, int cpPort, int agentPort, int fastPort, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);

        if (!IsProvisioningConfigured(config))
            return (false, "Provisioning host is not configured.", null);

        var projectName = $"agentfield-{jobId}";
        var hostUrl     = $"http://{config.ProvisionHost}:{cpPort}";
        var repoPath    = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
            ? DefaultSweAfRepoPath
            : config.SweAfRepoPath;

        logger.LogInformation(
            "Provisioning per-job SWE-AF stack (project={Project}, ports={Cp}/{Agent}/{Fast}) on {Host}",
            projectName, cpPort, agentPort, fastPort, config.ProvisionHost);

        // Step 1: Write .env (includes AGENTFIELD_PORT for port mapping)
        var anthropicBaseUrl = await ResolveAnthropicBaseUrlAsync(config, ct);
        var writeEnvCmd = BuildWriteEnvCommand(config, repoPath, anthropicBaseUrl, cpPort);
        var (envOut, envErr, envCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(writeEnvCmd, ct)
            : await ExecSshShellAsync(config, writeEnvCmd, ct);

        if (envCode != 0)
        {
            var err = (envErr ?? envOut ?? "Failed to write .env").Trim();
            logger.LogWarning("Per-job .env write failed on {Host}: {Error}", config.ProvisionHost, err);
            return (false, $"Failed to write .env: {err}", null);
        }

        // Step 2: Write docker-compose.hub.yml — overrides ports and inter-service env vars for all
        // three services so each build runs on its own block of 3 consecutive ports and the agents
        // can reach each other via the host IP.  Uses !override (Docker Compose 2.24.0+) so the
        // allocated ports replace (not append) the hardcoded values in the base compose file.
        var writeHubOverrideCmd = BuildWriteHubPortOverrideCommand(
            repoPath, cpPort, agentPort, fastPort);
        var (hubOvOut, hubOvErr, hubOvCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(writeHubOverrideCmd, ct)
            : await ExecSshShellAsync(config, writeHubOverrideCmd, ct);

        if (hubOvCode != 0)
        {
            var err = (hubOvErr ?? hubOvOut ?? "Failed to write docker-compose.hub.yml").Trim();
            logger.LogWarning("Per-job hub override write failed: {Error}", err);
            return (false, $"Failed to write docker-compose.hub.yml: {err}", null);
        }

        // Step 3: Write docker-compose.override.yml if configured
        var hasUserOverride = !string.IsNullOrWhiteSpace(config.ComposeOverride);
        if (hasUserOverride)
        {
            var writeOverrideCmd = BuildWriteOverrideCommand(config.ComposeOverride!, repoPath);
            var (ovOut, ovErr, ovCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(writeOverrideCmd, ct)
                : await ExecSshShellAsync(config, writeOverrideCmd, ct);

            if (ovCode != 0)
            {
                var err = (ovErr ?? ovOut ?? "Failed to write docker-compose.override.yml").Trim();
                logger.LogWarning("Per-job override write failed: {Error}", err);
                return (false, $"Failed to write docker-compose.override.yml: {err}", null);
            }
        }

        // Step 3.5: Write and download Caveman skill files if enabled
        var hasCaveman = config.CavemanEnabled;
        var cavemanOverridePath = $"{repoPath}/docker-compose.caveman.yml";
        if (hasCaveman)
        {
            // Download skill files to host cache
            var writeCavemanCmd = BuildWriteCavemanFilesCommand(repoPath);
            var (cavemanOut, cavemanErr, cavemanCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(writeCavemanCmd, ct)
                : await ExecSshShellAsync(config, writeCavemanCmd, ct);

            if (cavemanCode != 0)
            {
                var err = (cavemanErr ?? cavemanOut ?? "Failed to download Caveman files").Trim();
                logger.LogWarning("Caveman files write failed on {Host}: {Error}", config.ProvisionHost, err);
                return (false, $"Failed to write Caveman skill files: {err}", null);
            }

            // Write docker-compose.caveman.yml with bind mounts
            var cacheDir = "/tmp/caveman-skills";
            var cavemanYaml = $"""
                services:
                  swe-agent:
                    volumes:
                      - {cacheDir}/skills:/root/.claude/skills:ro
                      - {cacheDir}/CLAUDE.md:/root/.claude/CLAUDE.md:ro
                  swe-fast:
                    volumes:
                      - {cacheDir}/skills:/root/.claude/skills:ro
                      - {cacheDir}/CLAUDE.md:/root/.claude/CLAUDE.md:ro
                """;
            var cavemanB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cavemanYaml.Trim()));
            var writeCavemanComposeCmd = $"echo {cavemanB64} | base64 -d > {cavemanOverridePath}";
            var (cavComposeOut, cavComposeErr, cavComposeCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(writeCavemanComposeCmd, ct)
                : await ExecSshShellAsync(config, writeCavemanComposeCmd, ct);

            if (cavComposeCode != 0)
            {
                var err = (cavComposeErr ?? cavComposeOut ?? "Failed to write docker-compose.caveman.yml").Trim();
                logger.LogWarning("Caveman compose write failed on {Host}: {Error}", config.ProvisionHost, err);
                return (false, $"Failed to write Caveman compose override: {err}", null);
            }
        }

        // Step 3.6: Write opencode.json and compose override if template is configured
        var hasOpencodeJob = !string.IsNullOrWhiteSpace(config.OpencodeJsonTemplate);
        if (hasOpencodeJob)
        {
            await ApplyOpencodeJsonConfigAsync(config, anthropicBaseUrl ?? "", repoPath, ct);
        }

        // Step 4: Bring up the stack under the unique project name (no down-first — brand new project).
        // Explicit -f chain so docker-compose.hub.yml is always included. The auto-loaded
        // docker-compose.override.yml is only added when the user has configured one.
        // docker-compose.caveman.yml and docker-compose.opencode.yml are included when enabled.
        var composeFilesParts = new List<string> { "-f docker-compose.yml", "-f docker-compose.hub.yml" };
        if (hasUserOverride) composeFilesParts.Add("-f docker-compose.override.yml");
        if (hasCaveman) composeFilesParts.Add("-f docker-compose.caveman.yml");
        if (hasOpencodeJob) composeFilesParts.Add("-f docker-compose.opencode.yml");
        var composeFiles = string.Join(" ", composeFilesParts);
        var upCmd = $"cd {repoPath} && docker compose --project-name {projectName} {composeFiles} up -d --build";
        var (compOut, compErr, compCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(upCmd, ct)
            : await ExecSshShellAsync(config, upCmd, ct);

        if (compCode != 0)
        {
            var err = (compErr ?? compOut ?? "docker compose failed").Trim();
            logger.LogWarning("Per-job docker compose up failed (project={Project}): {Error}", projectName, err);
            return (false, $"docker compose up failed: {err}", null);
        }

        // Log actual container port state for diagnostics
        var psCmd = $"cd {repoPath} && docker compose --project-name {projectName} {composeFiles} ps --format 'table {{{{.Name}}}}\\t{{{{.Ports}}}}'";
        var (psOut, _, _) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(psCmd, ct)
            : await ExecSshShellAsync(config, psCmd, ct);
        if (!string.IsNullOrWhiteSpace(psOut))
            logger.LogInformation("Per-job stack ports (project={Project}):\n{Ps}", projectName, psOut.Trim());

        logger.LogInformation(
            "Per-job SWE-AF stack started (project={Project}) -> {Url}", projectName, hostUrl);
        return (true, null, hostUrl);
    }

    /// <summary>
    /// Downloads Caveman skill files and CLAUDE.md to the provision host.
    /// Skips download if files already exist (idempotent).
    /// Returns a shell command that must be executed on the provision host.
    /// Files are cached at /tmp/caveman-skills on the host and bind-mounted into containers.
    /// </summary>
    internal static string BuildWriteCavemanFilesCommand(string repoPath)
    {
        var cacheDir = "/tmp/caveman-skills";
        var skillsUrl = "https://raw.githubusercontent.com/JuliusBrussee/caveman/main/skills";

        // Build the command using simple curl commands — no heredocs, no bash vars,
        // avoiding C# string interpolation escape issues.
        var lines = new List<string>();
        lines.Add("set -e");
        lines.Add("mkdir -p " + cacheDir + "/skills/caveman " + cacheDir + "/skills/caveman-commit " + cacheDir + "/skills/caveman-review " + cacheDir + "/skills/caveman-help");

        foreach (var skill in new[] { "caveman", "caveman-commit", "caveman-review", "caveman-help" })
        {
            lines.Add("[ -s " + cacheDir + "/skills/" + skill + "/SKILL.md ] || curl -fsSL " + skillsUrl + "/" + skill + "/SKILL.md -o " + cacheDir + "/skills/" + skill + "/SKILL.md");
        }

        // Write CLAUDE.md using a temp file approach with printf
        var cludeMdContent = "description: \"Caveman mode - ultra-compressed responses\"";
        lines.Add("mkdir -p " + cacheDir);
        lines.Add("printf '%s\\n' '---' '" + cludeMdContent + "' '---' '' 'You are operating in Caveman mode. Respond in ultra-compressed style: drop articles (a/an/the), fragments OK, short synonyms. Technical terms exact. Code blocks unchanged. Errors quoted exact. Pattern: [thing] [action] [reason]. [next step]. Never say sure, happy to, let me, simply.' '' 'Load all skills from ~/.claude/skills/.' > " + cacheDir + "/CLAUDE.md");

        var bash = string.Join("\n", lines);
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bash));
        return "echo " + b64 + " | base64 -d | bash";
    }

    /// <summary>
    /// Writes opencode.json from the configured template with {ProxyUrl} replaced,
    /// and generates a docker-compose.opencode.yml with bind mounts into all agent services.
    /// </summary>
    private async Task ApplyOpencodeJsonConfigAsync(SweAfConfigEntity config, string proxyUrl, string repoPath, CancellationToken ct)
    {
        var opencodePath = $"{repoPath}/opencode.json";
        var opencodeComposePath = $"{repoPath}/docker-compose.opencode.yml";
        var rendered = config.OpencodeJsonTemplate!.Replace("{ProxyUrl}", proxyUrl);

        logger.LogInformation("Writing opencode.json to {Path} on {Host}", opencodePath, config.ProvisionHost);

        var writeOpencodeCmd = BuildWriteFileCommand(rendered, opencodePath);
        var (opencodeOut, opencodeErr, opencodeCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(writeOpencodeCmd, ct)
            : await ExecSshShellAsync(config, writeOpencodeCmd, ct);

        if (opencodeCode != 0)
        {
            var errorMsg = (opencodeErr ?? opencodeOut ?? "Failed to write opencode.json").Trim();
            logger.LogWarning("Opencode JSON write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return;
        }

        var opencodeComposeYaml = $"""
            services:
              control-plane:
                volumes:
                  - {opencodePath}:/root/.opencode.json:ro
              swe-agent:
                volumes:
                  - {opencodePath}:/root/.opencode.json:ro
              swe-fast:
                volumes:
                  - {opencodePath}:/root/.opencode.json:ro
            """;
        var opencodeComposeB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(opencodeComposeYaml.Trim()));
        var writeOpencodeComposeCmd = $"echo {opencodeComposeB64} | base64 -d > {opencodeComposePath}";
        var (composeOut, composeErr, composeCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(writeOpencodeComposeCmd, ct)
            : await ExecSshShellAsync(config, writeOpencodeComposeCmd, ct);

        if (composeCode != 0)
        {
            var errorMsg = (composeErr ?? composeOut ?? "Failed to write docker-compose.opencode.yml").Trim();
            logger.LogWarning("Opencode compose write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
        }
    }

    /// <summary>
    /// Builds a shell command that writes arbitrary content to a file,
    /// using base64 encoding to safely transport content without shell escaping issues.
    /// </summary>
    internal static string BuildWriteFileCommand(string content, string filePath)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        return $"echo {b64} | base64 -d > {filePath}";
    }

    /// <summary>
    /// Tears down the compose stack for a specific build job by project name.
    /// Returns an error message on failure, or null on success.
    /// </summary>
    public async Task<string?> StopControlPlaneForJobAsync(
        string projectName, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);

        if (!IsProvisioningConfigured(config))
            return "Provisioning host is not configured.";

        var repoPath = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
            ? DefaultSweAfRepoPath
            : config.SweAfRepoPath;

        logger.LogInformation(
            "Stopping per-job SWE-AF stack (project={Project}) on {Host}", projectName, config.ProvisionHost);

        var downCmd = $"cd {repoPath} && docker compose --project-name {projectName} down";
        var (stdout, stderr, exitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(downCmd, ct)
            : await ExecSshShellAsync(config, downCmd, ct);

        if (exitCode != 0)
        {
            var err = (stderr ?? stdout ?? "docker compose down failed").Trim();
            logger.LogWarning("Per-job compose down failed (project={Project}): {Error}", projectName, err);
            return err;
        }

        logger.LogInformation("Per-job SWE-AF stack stopped (project={Project})", projectName);
        return null;
    }

    /// <summary>
    /// Lists the names of running Docker Compose projects on the provision host whose
    /// names match the pattern "agentfield-{number}". Used by the recovery service
    /// to detect and clean up orphaned per-build containers.
    /// Returns an empty list on error (logs a warning).
    /// </summary>
    public async Task<List<string>> ListActiveComposeProjectsAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);
        if (!IsProvisioningConfigured(config))
            return [];

        // "docker compose ls" lists all known projects (running or stopped).
        // We filter to those whose name matches our per-job naming convention.
        var lsCmd = "docker compose ls --all --format json";
        string? output;
        try
        {
            var (stdout, stderr, exitCode) = IsLocalHost(config.ProvisionHost!)
                ? await ExecLocalShellAsync(lsCmd, ct)
                : await ExecSshShellAsync(config, lsCmd, ct);

            if (exitCode != 0)
            {
                logger.LogWarning("docker compose ls failed on {Host}: {Error}",
                    config.ProvisionHost, (stderr ?? stdout ?? "").Trim());
                return [];
            }

            output = stdout;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list compose projects on {Host}", config.ProvisionHost);
            return [];
        }

        return ParseComposeProjectNames(output);
    }

    /// <summary>
    /// Fetches the last <paramref name="lines"/> lines of combined stdout/stderr from all
    /// containers in the given Compose project.
    /// </summary>
    public async Task<(string? Logs, string? Error)> GetContainerLogsAsync(
        string projectName, int lines = 200, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);
        if (!IsProvisioningConfigured(config))
            return (null, "Provisioning host is not configured.");

        var repoPath = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
            ? DefaultSweAfRepoPath
            : config.SweAfRepoPath;
        var cmd = $"cd {repoPath} && docker compose --project-name {projectName} logs --tail={lines} --no-color 2>&1";
        var (stdout, stderr, exitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(cmd, ct)
            : await ExecSshShellAsync(config, cmd, ct);

        if (exitCode != 0)
        {
            var err = (stderr ?? stdout ?? "docker compose logs failed").Trim();
            logger.LogWarning("docker compose logs failed (project={Project}): {Error}", projectName, err);
            return (null, err);
        }

        return (stdout?.Trim(), null);
    }

    private static List<string> ParseComposeProjectNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            // docker compose ls --format json returns an array like:
            // [{"Name":"agentfield-42","Status":"running","ConfigFiles":"/path/…"}]
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];

            var results = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("Name", out var nameProp))
                    continue;
                var name = nameProp.GetString();
                if (name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"^agentfield-\d+$"))
                    results.Add(name);
            }
            return results;
        }
        catch
        {
            // Fallback: parse line by line (some older Compose versions output differently)
            var results = new List<string>();
            foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^agentfield-\d+"))
                {
                    var name = trimmed.Split(' ', '\t')[0];
                    results.Add(name);
                }
            }
            return results;
        }
    }

    // ── Shared control plane (legacy) ─────────────────────────────────────────

    // /// <summary>
    // /// Tears down the AgentField control plane on the configured host.
    // /// Returns an error message on failure, or null on success.
    // /// </summary>
    // public async Task<string?> StopControlPlaneAsync(CancellationToken ct = default)
    // {
    //     var config = await GetConfigAsync(ct);
    //
    //     if (!IsProvisioningConfigured(config))
    //         return "Provisioning host is not configured.";
    //
    //     var repoPath = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
    //         ? DefaultSweAfRepoPath
    //         : config.SweAfRepoPath;
    //
    //     logger.LogInformation("Stopping SWE-AF stack via docker compose down on {Host}", config.ProvisionHost);
    //
    //     var downCmd = $"cd {repoPath} && docker compose down";
    //     var (stdout, stderr, exitCode) = IsLocalHost(config.ProvisionHost!)
    //         ? await ExecLocalShellAsync(downCmd, ct)
    //         : await ExecSshShellAsync(config, downCmd, ct);
    //
    //     if (exitCode != 0)
    //     {
    //         var errorMsg = (stderr ?? stdout ?? "docker compose down failed").Trim();
    //         logger.LogWarning("docker compose down failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
    //         return errorMsg;
    //     }
    //
    //     logger.LogInformation("SWE-AF stack stopped on {Host}", config.ProvisionHost);
    //     return null;
    // }

    private bool IsProvisioningConfigured(SweAfConfigEntity config) =>
        !string.IsNullOrWhiteSpace(config.ProvisionHost);

    private async Task<string?> ResolveAnthropicBaseUrlAsync(SweAfConfigEntity config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.LlmDeploymentId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployment = await db.LlmDeployments
            .FirstOrDefaultAsync(d => d.DeploymentId == config.LlmDeploymentId, ct);
        if (deployment is null)
            return null;

        var host = await db.GpuHosts
            .FirstOrDefaultAsync(h => h.HostId == deployment.HostId, ct);
        if (host is null)
            return null;

        // Prefer the nginx proxy when configured — it round-robins across all running
        // vLLM instances on the host rather than pinning to a single container port.
        if (host.ProxyPort.HasValue)
            return $"http://{host.Host}:{host.ProxyPort}";

        return $"http://{host.Host}:{deployment.HostPort}";
    }

    /// <summary>
    /// Builds a shell command that writes a docker-compose.override.yml file,
    /// using base64 encoding to safely transport arbitrary YAML without shell escaping issues.
    /// </summary>
    private static string BuildWriteOverrideCommand(string overrideYaml, string repoPath)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(overrideYaml));
        return $"echo {b64} | base64 -d > {repoPath}/docker-compose.override.yml";
    }

    /// <summary>
    /// Builds a shell command that writes docker-compose.hub.yml — a hub-managed override that
    /// assigns each service its own host port from the allocated 3-port block and sets the
    /// environment variables so agents discover each other via Docker service DNS (within the
    /// compose project network) rather than host IP.  This avoids host-port conflicts between
    /// parallel builds and is immune to changes in the allocated port numbers.
    /// Uses !override (Docker Compose 2.24.0+) so the allocated ports replace (not append)
    /// the hardcoded values in the base compose file.
    /// </summary>
    private static string BuildWriteHubPortOverrideCommand(
        string repoPath, int cpPort, int agentPort, int fastPort)
    {
        var yaml = $"""
            services:
              control-plane:
                ports: !override
                  - "{cpPort}:8080"
              swe-agent:
                ports: !override
                  - "{agentPort}:8003"
                environment:
                  AGENTFIELD_SERVER: "http://control-plane:8080"
                  AGENT_CALLBACK_URL: "http://swe-agent:8003"
                  
              swe-fast:
                ports: !override
                  - "{fastPort}:8004"
                environment:
                  AGENTFIELD_SERVER: "http://control-plane:8080"
                  AGENT_CALLBACK_URL: "http://swe-fast:8004"
            """;
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(yaml));
        return $"echo {b64} | base64 -d > {repoPath}/docker-compose.hub.yml";
    }

    /// <summary>
    /// Builds a shell command that writes a .env file for docker compose,
    /// using single-quoted heredoc so values are never shell-expanded.
    /// When port is provided, writes AGENTFIELD_PORT so the compose file can map the correct host port.
    /// </summary>
    internal static string BuildWriteEnvCommand(
        SweAfConfigEntity config, string repoPath, string? anthropicBaseUrl, int? port = null)
    {
        var lines = new List<string>();

        // Runtime-specific API key and base URL
        if (config.Runtime == "openrouter")
        {
            if (!string.IsNullOrWhiteSpace(config.OpenRouterEndpointUrl))
                lines.Add($"ANTHROPIC_BASE_URL={config.OpenRouterEndpointUrl}");
            if (!string.IsNullOrWhiteSpace(config.OpenRouterApiKey))
                lines.Add($"ANTHROPIC_API_KEY={config.OpenRouterApiKey}");
        }
        else if (config.Runtime == "claude_code")
        {
            if (!string.IsNullOrWhiteSpace(anthropicBaseUrl))
                lines.Add($"ANTHROPIC_BASE_URL={anthropicBaseUrl}");
            if (!string.IsNullOrWhiteSpace(config.AnthropicApiKey))
                lines.Add($"ANTHROPIC_API_KEY={config.AnthropicApiKey}");
        }
        // open_code runtime: no API key injected; base URL comes from LlmDeployment (already in anthropicBaseUrl)
        else
        {
            if (!string.IsNullOrWhiteSpace(anthropicBaseUrl))
                lines.Add($"ANTHROPIC_BASE_URL={anthropicBaseUrl}");
        }

        // Always-injected fields
        if (!string.IsNullOrWhiteSpace(config.RepositoryApiToken))
            lines.Add($"GH_TOKEN={config.RepositoryApiToken}");
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            lines.Add($"CLAUDE_CODE_OAUTH_TOKEN={config.ApiKey}");
        if (port.HasValue)
            lines.Add($"AGENTFIELD_PORT={port.Value}");
        if (!string.IsNullOrWhiteSpace(config.ControlPlaneImageTag))
            lines.Add($"AGENTFIELD_IMAGE_TAG={config.ControlPlaneImageTag}");
        lines.Add($"CLAUDE_CODE_MAX_OUTPUT_TOKENS=32000");

        var quoted = string.Join(" ", lines.Select(l => "'" + l.Replace("'", "'\\''") + "'"));
        return $"printf '%s\\n' {quoted} > {repoPath}/.env";
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecLocalShellAsync(
        string command, CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C {command}")
                : new ProcessStartInfo("bash", $"-c {ShellQuote(command)}");

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return (stdout, stderr, proc.ExitCode);
        }
        catch (OperationCanceledException) { return (null, "Operation cancelled.", 1); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local shell command failed");
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecSshShellAsync(
        SweAfConfigEntity config, string command, CancellationToken ct, bool useSudo = true)
    {
        var auth = BuildAuth(config);
        if (auth is null)
            return (null, "No SSH authentication configured (set SshKeyPath or SshPassword).", 1);

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(
                config.ProvisionHost!, config.SshPort, config.SshUser!, auth);

            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct);

            string? stdout, stderr;
            int exitCode;

            if (useSudo && config.RequiresSudo && !string.IsNullOrWhiteSpace(config.SudoPassword))
            {
                (stdout, stderr, exitCode) = await ExecuteWithSudoAsync(client, command, config.SudoPassword!, ct);
            }
            else
            {
                using var cmd = client.RunCommand(command);
                stdout = cmd.Result;
                stderr = cmd.Error;
                exitCode = cmd.ExitStatus ?? 0;
            }

            client.Disconnect();
            return (stdout, stderr, exitCode);
        }
        catch (OperationCanceledException) { return (null, "Operation cancelled.", 1); }
        catch (Exception ex)
        {
            logger.LogError(ex, "SSH shell command failed on {Host}", config.ProvisionHost);
            return (null, ex.Message, 1);
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecLocalDockerAsync(
        string dockerArgs, CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C docker {dockerArgs}")
                : new ProcessStartInfo("bash", $"-c \"docker {dockerArgs.Replace("\"", "\\\"")}\"");

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return (stdout, stderr, proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return (null, "Operation cancelled.", 1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local Docker command failed");
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecSshDockerAsync(
        SweAfConfigEntity config, string dockerArgs, CancellationToken ct)
    {
        var auth = BuildAuth(config);
        if (auth is null)
        {
            return (null, "No SSH authentication configured (set SshKeyPath or SshPassword).", 1);
        }

        var requiresSudo = config.RequiresSudo;
        var finalCommand = $"docker {dockerArgs}";

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(
                config.ProvisionHost!,
                config.SshPort,
                config.SshUser!,
                auth);

            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct);

            string? stdout;
            string? stderr;
            int exitCode;

            if (requiresSudo && !string.IsNullOrWhiteSpace(config.SudoPassword))
            {
                (stdout, stderr, exitCode) = await ExecuteWithSudoAsync(
                    client, finalCommand, config.SudoPassword!, ct);
            }
            else if (requiresSudo)
            {
                client.Disconnect();
                return (null, "Sudo is required for Docker but no sudo password configured.", 1);
            }
            else
            {
                using var cmd = client.RunCommand(finalCommand);
                stdout = cmd.Result;
                stderr = cmd.Error;
                exitCode = cmd.ExitStatus ?? 0;
            }

            client.Disconnect();
            return (stdout, stderr, exitCode);
        }
        catch (OperationCanceledException)
        {
            return (null, "Operation cancelled.", 1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SSH Docker command failed on {Host}", config.ProvisionHost);
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecuteWithSudoAsync(
        SshClient client, string command, string sudoPassword, CancellationToken ct)
    {
        try
        {
            var sudoCommand = $"echo '{sudoPassword}' | sudo -S sh -c \"{command}\"";

            using var cmd = client.RunCommand(sudoCommand);
            var stdout = cmd.Result;
            var stderr = cmd.Error;
            var exitCode = cmd.ExitStatus ?? 0;

            return (stdout, stderr, exitCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute sudo command via SSH");
            return (null, ex.Message, 1);
        }
    }

    private AuthenticationMethod? BuildAuth(SweAfConfigEntity config)
    {
        if (!string.IsNullOrWhiteSpace(config.SshKeyPath))
        {
            var path = config.SshKeyPath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new PrivateKeyAuthenticationMethod(
                config.SshUser!,
                new PrivateKeyFile(path));
        }

        if (!string.IsNullOrWhiteSpace(config.SshPassword))
        {
            return new PasswordAuthenticationMethod(
                config.SshUser!,
                config.SshPassword!);
        }

        return null;
    }

    private async Task<SweAfConfigEntity> GetConfigAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfConfigs.FirstOrDefaultAsync(ct) ?? new SweAfConfigEntity();
    }
}
