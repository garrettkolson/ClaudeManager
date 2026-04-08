using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Services.Docker;
using Microsoft.EntityFrameworkCore;

// Wire SessionStore → PersistenceQueue after both are built

var builder = WebApplication.CreateBuilder(args);

// Bind to all network interfaces so LAN clients (e.g. AgentField) can reach the hub.
// Port is read from Hub:HttpPort in config, defaulting to 5258.
var httpPort = builder.Configuration.GetValue("Hub:HttpPort", 5258);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(httpPort));

// ── Database ──────────────────────────────────────────────────────────────────

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ClaudeManager",
    "claude_manager.db");

Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContextFactory<ClaudeManagerDbContext>(options =>
    options
        .UseSqlite($"Data Source={dbPath}")
        .EnableDetailedErrors(builder.Environment.IsDevelopment()));

// ── Known machines (from config) ──────────────────────────────────────────────

var knownMachines = builder.Configuration
    .GetSection("KnownMachines")
    .Get<List<KnownMachineConfig>>() ?? [];

builder.Services.AddSingleton<IReadOnlyList<KnownMachineConfig>>(knownMachines);
builder.Services.AddSingleton<AgentLaunchService>();

// ── Core services ─────────────────────────────────────────────────────────────

builder.Services.AddSingleton<DashboardNotifier>();
builder.Services.AddSingleton<SessionStore>();

// Persistence: StartupRecoveryService must be registered before PersistenceQueue
// so it runs first during IHostedService startup
builder.Services.AddHostedService<StartupRecoveryService>();
builder.Services.AddSingleton<PersistenceQueue>();
builder.Services.AddSingleton<IPersistenceQueue>(sp => sp.GetRequiredService<PersistenceQueue>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersistenceQueue>());
builder.Services.AddHostedService<DbPruningService>();
builder.Services.AddSingleton<WikiService>();
builder.Services.AddSingleton<IWikiService>(sp => sp.GetRequiredService<WikiService>());
builder.Services.AddSingleton<AgentCommandService>();
builder.Services.AddSingleton<GpuHostService>();
builder.Services.AddSingleton<HubSecretService>();
builder.Services.AddSingleton<LlmGpuDiscoveryService>();
builder.Services.AddSingleton<LlmDeploymentNotifier>();
builder.Services.AddHttpClient(); // For LlmInstanceService health checks
builder.Services.AddSingleton<IDockerExecutor, DockerExecutor>(services =>
{
    var logger = services.GetRequiredService<ILogger<DockerExecutor>>();
    return new DockerExecutor(logger);
});
builder.Services.AddSingleton<LlmInstanceService>();
builder.Services.AddSingleton<NginxProxyService>();
builder.Services.AddSingleton<LlmProxyConfigService>();
builder.Services.AddSingleton<LlmDeploymentService>();
builder.Services.AddSingleton<LlmDeploymentHealthService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LlmDeploymentHealthService>());
builder.Services.AddHttpClient<ModelConfigFetcher>();

// ── SWE-AF / AgentField ───────────────────────────────────────────────────────
// Config and hosts are now stored in the DB and managed via the SWE-AF Servers UI.
// SweAfConfigService must be started before SweAfRecoveryService so the IsConfigured
// sync property is populated before recovery tries to read it.

builder.Services.AddSingleton<SweAfConfigService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SweAfConfigService>());
builder.Services.AddHttpClient("sweaf");
builder.Services.AddSingleton<BuildNotifier>();
builder.Services.AddSingleton<SweAfPortAllocator>();
builder.Services.AddSingleton<SweAfProvisioningService>();
builder.Services.AddSingleton<SweAfService>();
builder.Services.AddSingleton<SweAfHostService>();
builder.Services.AddHostedService<SweAfRecoveryService>();
builder.Services.AddSingleton<NotificationService>();

// ── SignalR ───────────────────────────────────────────────────────────────────

builder.Services.AddSignalR(opts =>
    opts.MaximumReceiveMessageSize = 10 * 1024 * 1024); // 10 MB

// ── Blazor ────────────────────────────────────────────────────────────────────

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Link SessionStore to PersistenceQueue now that both singletons are resolved
app.Services.GetRequiredService<SessionStore>()
   .SetPersistenceQueue(app.Services.GetRequiredService<IPersistenceQueue>());

app.UseMiddleware<AgentSecretMiddleware>();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapHub<AgentHub>("/agenthub");

// ── Wiki REST API (used by ClaudeManager.McpServer on agent machines) ─────────

var agentSecret = app.Configuration["AgentSecret"]!;

app.MapGet("/api/wiki", async (WikiService wiki) =>
{
    var entries = await wiki.GetAllAsync();
    return Results.Ok(entries
        .Where(e => !e.IsArchived)
        .Select(e => new { e.Id, e.Title, e.Category, e.Tags }));
}).AddEndpointFilter(AgentSecretFilter(agentSecret));

app.MapPost("/api/wiki/save", async (WikiService wiki, WikiSaveRequest req) =>
{
    await wiki.UpsertByTitleAsync(req.Title, req.Category, req.Content, req.Tags);
    return Results.Ok();
}).AddEndpointFilter(AgentSecretFilter(agentSecret));

// ── LLM proxy config (for agents to fetch at startup) ────────────────────────

app.MapGet("/api/llm-config", async (LlmProxyConfigService svc) =>
{
    var config = await svc.GetConfigAsync();
    return Results.Ok(config);
}).AddEndpointFilter(AgentSecretFilter(agentSecret));

// ── AgentField observability webhook ──────────────────────────────────────────

app.MapPost("/api/webhooks/agentfield", async (HttpRequest req, SweAfService svc, SweAfConfigService cfgSvc, ILoggerFactory logFac) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    var signature = req.Headers["X-AgentField-Signature"].FirstOrDefault();
    if (!SweAfService.VerifySignature(cfgSvc.WebhookSecret, body, signature))
        return Results.Unauthorized();

    ObservabilityBatch? batch;
    try
    {
        batch = JsonSerializer.Deserialize<ObservabilityBatch>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid payload");
    }
    if (batch is null) return Results.BadRequest("Invalid payload");

    // Acknowledge immediately — AgentField has a short HTTP deadline and will
    // retry on timeout. Processing is fast but any DB latency would cause
    // "context deadline exceeded" errors on the caller side.
    var logger = logFac.CreateLogger("AgentFieldWebhook");
    _ = Task.Run(async () =>
    {
        try   { await svc.ProcessWebhookBatchAsync(batch); }
        catch (Exception ex) { logger.LogError(ex, "Webhook batch processing failed for batch {BatchId}", batch.BatchId); }
    });

    return Results.Ok();
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Log LAN addresses at startup so the correct HubPublicUrl is easy to find.
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var lanIps = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up
             && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
    .Select(a => a.Address.ToString())
    .ToList();
if (lanIps.Count > 0)
    startupLog.LogInformation(
        "Hub listening on port {Port}. Set HubPublicUrl to one of: {Urls}",
        httpPort,
        string.Join(", ", lanIps.Select(ip => $"http://{ip}:{httpPort}")));

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────

static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>>
    AgentSecretFilter(string secret) =>
    async (ctx, next) =>
    {
        var supplied = ctx.HttpContext.Request.Headers["X-Agent-Secret"].FirstOrDefault()
                    ?? ctx.HttpContext.Request.Query["secret"].FirstOrDefault();
        if (supplied != secret)
            return Results.Unauthorized();
        return await next(ctx);
    };

record WikiSaveRequest(string Title, string Category, string Content, string? Tags);
