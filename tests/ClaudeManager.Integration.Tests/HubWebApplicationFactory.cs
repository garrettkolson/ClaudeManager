using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeManager.Integration.Tests;

/// <summary>
/// Spins up the real Hub application on an in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>.
/// A temporary SQLite file replaces the production database path so <c>MigrateAsync</c> works
/// and test data is isolated from production.
/// </summary>
public sealed class HubWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestSecret = "integration-test-secret";

    private readonly string _tempDbPath =
        Path.Combine(Path.GetTempPath(), $"cm_integration_{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Provide AgentSecret so AgentSecretMiddleware doesn't throw on startup
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentSecret"] = TestSecret,
            }));

        builder.ConfigureServices(services =>
        {
            // Replace the production DbContextFactory with one pointing at the temp file.
            // Remove both the factory and the options so no stale connection string leaks through.
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(IDbContextFactory<ClaudeManagerDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions<ClaudeManagerDbContext>))
                .ToList();
            foreach (var d in toRemove)
                services.Remove(d);

            services.AddDbContextFactory<ClaudeManagerDbContext>(opts =>
                opts.UseSqlite($"Data Source={_tempDbPath}"));
        });
    }

    /// <summary>
    /// Creates a SignalR <see cref="HubConnection"/> that connects to the in-process hub
    /// using long polling (compatible with <c>TestServer.CreateHandler()</c>).
    /// Pass <see langword="null"/> for <paramref name="secret"/> to omit the auth header.
    /// </summary>
    public HubConnection CreateAgentConnection(string? secret = TestSecret) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/agenthub"), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                opts.Transports = HttpTransportType.LongPolling;
                if (secret is not null)
                    opts.Headers["X-Agent-Secret"] = secret;
            })
            .Build();

    public SessionStore SessionStore => Services.GetRequiredService<SessionStore>();
    public AgentCommandService CommandService => Services.GetRequiredService<AgentCommandService>();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); }
            catch { /* best-effort cleanup */ }
        }
    }
}
