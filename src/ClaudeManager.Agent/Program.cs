using System.Text.Json;
using ClaudeManager.Agent;
using ClaudeManager.Agent.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    //.ConfigureHostConfiguration(config => config.AddJsonFile("appsettings.json").Build())
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration.GetSection("Agent").Get<AgentConfig>()
            ?? throw new InvalidOperationException("Missing 'Agent' configuration section.");

        services.AddSingleton(config);
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var config = host.Services.GetRequiredService<AgentConfig>();

// ── Startup validation ────────────────────────────────────────────────────────

var machineId = MachineIdProvider.GetOrCreate();
logger.LogInformation("Machine ID: {MachineId}", machineId);

var binary = ClaudeValidator.ResolveBinary(config.ClaudeBinaryPath);
if (binary is null)
{
    logger.LogCritical("Cannot find 'claude' binary. Set ClaudeBinaryPath in appsettings.json or ensure it is on PATH.");
    return 1;
}

logger.LogInformation("Validating claude authentication...");
var validator      = new ClaudeValidator(new ProcessRunner());
var (ok, error)    = await validator.ValidateAsync(config.ClaudeBinaryPath, CancellationToken.None);
if (!ok)
{
    logger.LogCritical("Claude validation failed: {Error}", error);
    return 1;
}
logger.LogInformation("Claude authenticated successfully.");

// ── MCP config (wiki tools for Claude) ───────────────────────────────────────

string? mcpConfigPath = null;

if (config.McpServerPath is not null)
{
    if (!File.Exists(config.McpServerPath))
    {
        logger.LogWarning(
            "McpServerPath '{Path}' not found; wiki MCP tools will be disabled.",
            config.McpServerPath);
    }
    else
    {
        mcpConfigPath = Path.Combine(Path.GetTempPath(), "claude_manager_mcp.json");

        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["claude-manager-wiki"] = new
                {
                    command = config.McpServerPath,
                    env = new Dictionary<string, string>
                    {
                        ["CM_HUB_URL"]        = config.HubUrl,
                        ["CM_AGENT_SECRET"]   = config.SharedSecret,
                    },
                },
            },
        };

        await File.WriteAllTextAsync(
            mcpConfigPath,
            JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true }));

        logger.LogInformation("MCP config written to {Path}", mcpConfigPath);
    }
}

// ── LLM endpoint overrides ────────────────────────────────────────────────────

var extraEnv = new Dictionary<string, string>();
if (!string.IsNullOrWhiteSpace(config.ClaudeBaseUrl))
    extraEnv["ANTHROPIC_BASE_URL"] = config.ClaudeBaseUrl;
if (!string.IsNullOrWhiteSpace(config.ClaudeApiKey))
    extraEnv["ANTHROPIC_API_KEY"] = config.ClaudeApiKey;

// ── Run ───────────────────────────────────────────────────────────────────────

using var agentHost = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton(config);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ClaudeValidator>();
        services.AddSingleton<IClaudeProcessFactory>(sp =>
            new ClaudeProcessFactory(binary, sp.GetRequiredService<ILoggerFactory>(), mcpConfigPath,
                extraEnv.Count > 0 ? extraEnv : null));
        services.AddSingleton(sp =>
            new SessionProcessManager(
                sp.GetRequiredService<IClaudeProcessFactory>(),
                sp.GetRequiredService<ILogger<SessionProcessManager>>()));
        services.AddHostedService(sp =>
            new AgentService(
                config,
                machineId,
                sp.GetRequiredService<SessionProcessManager>(),
                sp.GetRequiredService<ILogger<AgentService>>()));
    })
    .Build();

await agentHost.RunAsync();
return 0;
