using ClaudeManager.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var hubUrl = Environment.GetEnvironmentVariable("CM_HUB_URL")
    ?? throw new InvalidOperationException(
        "CM_HUB_URL environment variable is required.");
var secret = Environment.GetEnvironmentVariable("CM_AGENT_SECRET")
    ?? throw new InvalidOperationException(
        "CM_AGENT_SECRET environment variable is required.");

var builder = Host.CreateApplicationBuilder(args);

// Suppress non-error logging so it doesn't pollute the MCP stdio channel
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<WikiTools>();

builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton(new WikiConfig(hubUrl, secret));

await builder.Build().RunAsync();
