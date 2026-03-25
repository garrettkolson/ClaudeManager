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

var logger   = host.Services.GetRequiredService<ILogger<Program>>();
var config   = host.Services.GetRequiredService<AgentConfig>();

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
var (ok, error) = await ClaudeValidator.ValidateAsync(config.ClaudeBinaryPath, CancellationToken.None);
if (!ok)
{
    logger.LogCritical("Claude validation failed: {Error}", error);
    return 1;
}
logger.LogInformation("Claude authenticated successfully.");

// ── Run ───────────────────────────────────────────────────────────────────────

var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

using var agentHost = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton(config);
        services.AddSingleton(new AgentServiceArgs(machineId, binary));
        services.AddHostedService(sp =>
        {
            var args2 = sp.GetRequiredService<AgentServiceArgs>();
            return new AgentService(
                config,
                args2.MachineId,
                args2.Binary,
                sp.GetRequiredService<ILogger<AgentService>>(),
                sp.GetRequiredService<ILoggerFactory>());
        });
    })
    .Build();

await agentHost.RunAsync();
return 0;

internal record AgentServiceArgs(string MachineId, string Binary);
