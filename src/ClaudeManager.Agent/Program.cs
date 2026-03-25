using ClaudeManager.Agent;
using ClaudeManager.Agent.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
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

// ── Run ───────────────────────────────────────────────────────────────────────

using var agentHost = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton(config);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ClaudeValidator>();
        services.AddSingleton<IClaudeProcessFactory>(sp =>
            new ClaudeProcessFactory(binary, sp.GetRequiredService<ILoggerFactory>()));
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
