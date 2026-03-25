namespace ClaudeManager.Hub.Hubs;

/// <summary>
/// Rejects SignalR connections to /agenthub that don't supply the correct shared secret.
/// </summary>
public class AgentSecretMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _secret;

    public AgentSecretMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next   = next;
        _secret = config["AgentSecret"] ?? throw new InvalidOperationException(
            "AgentSecret is not configured. Set it in appsettings.json or as an environment variable.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/agenthub"))
        {
            var supplied = context.Request.Headers["X-Agent-Secret"].FirstOrDefault()
                        ?? context.Request.Query["secret"].FirstOrDefault();

            if (supplied != _secret)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }

        await _next(context);
    }
}
