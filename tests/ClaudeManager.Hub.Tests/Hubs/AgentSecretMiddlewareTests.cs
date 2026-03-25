using ClaudeManager.Hub.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace ClaudeManager.Hub.Tests.Hubs;

[TestFixture]
public class AgentSecretMiddlewareTests
{
    private const string CorrectSecret = "test-secret-123";

    private static AgentSecretMiddleware CreateMiddleware(RequestDelegate next)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "AgentSecret", CorrectSecret } })
            .Build();
        return new AgentSecretMiddleware(next, config);
    }

    private static DefaultHttpContext AgentHubContext() =>
        new() { Request = { Path = "/agenthub" }, Response = { Body = new MemoryStream() } };

    [Test]
    public async Task InvokeAsync_AgentHubPath_CorrectHeader_CallsNext()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = AgentHubContext();
        ctx.Request.Headers["X-Agent-Secret"] = CorrectSecret;

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task InvokeAsync_AgentHubPath_CorrectHeader_DoesNotReturn401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var ctx = AgentHubContext();
        ctx.Request.Headers["X-Agent-Secret"] = CorrectSecret;

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().NotBe(401);
    }

    [Test]
    public async Task InvokeAsync_AgentHubPath_MissingHeader_Returns401()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = AgentHubContext();

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        nextCalled.Should().BeFalse();
    }

    [Test]
    public async Task InvokeAsync_AgentHubPath_WrongHeader_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var ctx = AgentHubContext();
        ctx.Request.Headers["X-Agent-Secret"] = "wrong-secret";

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task InvokeAsync_AgentHubPath_CorrectQueryParam_CallsNext()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = AgentHubContext();
        ctx.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues> { { "secret", CorrectSecret } });

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task InvokeAsync_AgentHubPath_MissingHeaderAndQuery_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var ctx = AgentHubContext();

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
    }

    [Test]
    public async Task InvokeAsync_NonAgentHubPath_NoHeader_CallsNext()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext
        {
            Request  = { Path = "/some-other-path" },
            Response = { Body = new MemoryStream() },
        };

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task InvokeAsync_NonAgentHubPath_NoHeader_DoesNotReturn401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var ctx = new DefaultHttpContext
        {
            Request  = { Path = "/dashboard" },
            Response = { Body = new MemoryStream() },
        };

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().NotBe(401);
    }
}
