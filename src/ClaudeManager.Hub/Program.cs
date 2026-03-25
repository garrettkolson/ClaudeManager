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
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
