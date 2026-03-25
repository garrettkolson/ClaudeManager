using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Services;
using Microsoft.EntityFrameworkCore;

// Wire SessionStore → PersistenceQueue after both are built

var builder = WebApplication.CreateBuilder(args);

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

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

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
