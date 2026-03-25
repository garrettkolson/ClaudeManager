using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeManager.McpServer;
using FluentAssertions;

namespace ClaudeManager.McpServer.Tests;

/// <summary>
/// Tests WikiTools by injecting a mock HttpMessageHandler so no real HTTP calls are made.
/// </summary>
[TestFixture]
public class WikiToolsTests
{
    private const string HubUrl = "http://hub.test";
    private const string Secret = "test-secret";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (WikiTools tools, List<HttpRequestMessage> captured)
        BuildTools(HttpStatusCode statusCode, string responseBody = "")
    {
        var captured = new List<HttpRequestMessage>();
        var handler  = new MockHandler(req =>
        {
            captured.Add(req);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        });
        var http  = new HttpClient(handler);
        var tools = new WikiTools(http, new WikiConfig(HubUrl, Secret));
        return (tools, captured);
    }

    // ── WikiSave ──────────────────────────────────────────────────────────────

    [Test]
    public async Task WikiSave_Success_ReturnsConfirmationMessage()
    {
        var (tools, _) = BuildTools(HttpStatusCode.OK);

        var result = await tools.WikiSave("Auth overhaul", "project", "content", "auth");

        result.Should().Contain("Auth overhaul");
    }

    [Test]
    public async Task WikiSave_Success_PostsToCorrectEndpoint()
    {
        var (tools, captured) = BuildTools(HttpStatusCode.OK);

        await tools.WikiSave("T", "note", "C", null);

        captured.Should().HaveCount(1);
        captured[0].RequestUri!.ToString().Should().Be($"{HubUrl}/api/wiki/save");
        captured[0].Method.Should().Be(HttpMethod.Post);
    }

    [Test]
    public async Task WikiSave_Success_SendsAgentSecretHeader()
    {
        var (tools, captured) = BuildTools(HttpStatusCode.OK);

        await tools.WikiSave("T", "note", "C", null);

        captured[0].Headers.GetValues("X-Agent-Secret").Should().Contain(Secret);
    }

    [Test]
    public async Task WikiSave_HttpError_ReturnsErrorMessage()
    {
        var (tools, _) = BuildTools(HttpStatusCode.InternalServerError, "db error");

        var result = await tools.WikiSave("T", "note", "C", null);

        result.Should().Contain("Failed");
        result.Should().Contain("500");
    }

    [Test]
    public async Task WikiSave_InvalidCategory_ReturnsErrorWithoutMakingRequest()
    {
        var (tools, captured) = BuildTools(HttpStatusCode.OK);

        var result = await tools.WikiSave("T", "unknown-category", "C", null);

        result.Should().Contain("Error");
        captured.Should().BeEmpty();
    }

    [TestCase("project")]
    [TestCase("decision")]
    [TestCase("bug")]
    [TestCase("note")]
    public async Task WikiSave_ValidCategory_MakesRequest(string category)
    {
        var (tools, captured) = BuildTools(HttpStatusCode.OK);

        await tools.WikiSave("T", category, "C", null);

        captured.Should().HaveCount(1);
    }

    // ── WikiList ──────────────────────────────────────────────────────────────

    private static string MakeListJson(params (long Id, string Title, string Category, string? Tags)[] entries)
    {
        var items = entries.Select(e => new { e.Id, e.Title, e.Category, e.Tags });
        return JsonSerializer.Serialize(items);
    }

    [Test]
    public async Task WikiList_Success_GetsCorrectEndpoint()
    {
        var (tools, captured) = BuildTools(HttpStatusCode.OK, MakeListJson());

        await tools.WikiList();

        captured[0].RequestUri!.ToString().Should().Be($"{HubUrl}/api/wiki");
        captured[0].Method.Should().Be(HttpMethod.Get);
    }

    [Test]
    public async Task WikiList_Success_SendsAgentSecretHeader()
    {
        var (tools, captured) = BuildTools(HttpStatusCode.OK, MakeListJson());

        await tools.WikiList();

        captured[0].Headers.GetValues("X-Agent-Secret").Should().Contain(Secret);
    }

    [Test]
    public async Task WikiList_EmptyResponse_ReturnsEmptyMessage()
    {
        var (tools, _) = BuildTools(HttpStatusCode.OK, MakeListJson());

        var result = await tools.WikiList();

        result.ToLower().Should().Contain("empty");
    }

    [Test]
    public async Task WikiList_WithEntries_ContainsTitlesGroupedByCategory()
    {
        var json = MakeListJson(
            (1, "Auth overhaul", "project", "auth"),
            (2, "Switch to JWT",  "decision", null));
        var (tools, _) = BuildTools(HttpStatusCode.OK, json);

        var result = await tools.WikiList();

        result.Should().Contain("Auth overhaul");
        result.Should().Contain("Switch to JWT");
        result.Should().Contain("project");
        result.Should().Contain("decision");
    }

    [Test]
    public async Task WikiList_HttpError_ReturnsErrorMessage()
    {
        var (tools, _) = BuildTools(HttpStatusCode.Unauthorized);

        var result = await tools.WikiList();

        result.Should().Contain("Failed");
        result.Should().Contain("401");
    }

    // ── Mock handler ──────────────────────────────────────────────────────────

    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
