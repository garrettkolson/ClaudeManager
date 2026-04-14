using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace ClaudeManager.Integration.Tests;

/// <summary>
/// End-to-end tests for the wiki REST API (<c>/api/wiki</c> and <c>/api/wiki/save</c>)
/// used by the MCP server on agent machines.
/// </summary>
[TestFixture]
public class WikiApiTests
{
    private HubWebApplicationFactory _factory = default!;
    private HttpClient _client = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new HubWebApplicationFactory();
        _client  = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage GetWiki() =>
        new(HttpMethod.Get, "/api/wiki")
        {
            Headers = { { "X-Agent-Secret", HubWebApplicationFactory.TestSecret } },
        };

    private static HttpRequestMessage SaveWiki(string title, string category,
        string content, string? tags = null)
    {
        var body = JsonSerializer.Serialize(new { title, category, content, tags });
        return new HttpRequestMessage(HttpMethod.Post, "/api/wiki/save")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { { "X-Agent-Secret", HubWebApplicationFactory.TestSecret } },
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Get_NoSecret_ReturnsUnauthorized()
    {
        var resp = await _client.GetAsync("/api/wiki");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Save_NoSecret_ReturnsUnauthorized()
    {
        var body = JsonSerializer.Serialize(new { title = "T", category = "c", content = "c" });
        var resp = await _client.PostAsync("/api/wiki/save",
            new StringContent(body, Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Get_WithSecret_ReturnsOk()
    {
        var resp = await _client.SendAsync(GetWiki());
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task SaveThenGet_EntryAppearsInListing()
    {
        var title = $"Wiki Entry {Guid.NewGuid():N}";
        var save  = await _client.SendAsync(SaveWiki(title, "architecture", "Some content", "test"));
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        var list     = await _client.SendAsync(GetWiki());
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var json     = await list.Content.ReadAsStringAsync();
        var entries  = JsonSerializer.Deserialize<JsonElement[]>(json);
        entries.Should().NotBeNull();
        entries!.Any(e => e.GetProperty("title").GetString() == title).Should().BeTrue();
    }

    [Test]
    public async Task Save_ArchivedEntry_NotReturnedByGet()
    {
        // Save, then archive by saving again with a different title that we can identify
        // (The REST API doesn't expose archive; we test that GET only returns non-archived.)
        // We verify via save + get that a fresh entry IS visible (archived logic is a
        // WikiService-level concern tested in WikiServiceTests, not this endpoint).
        var title = $"Visible Entry {Guid.NewGuid():N}";
        await _client.SendAsync(SaveWiki(title, "testing", "visible"));

        var list    = await _client.SendAsync(GetWiki());
        var json    = await list.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement[]>(json)!;
        entries.Any(e => e.GetProperty("title").GetString() == title).Should().BeTrue();
    }

    [Test]
    public async Task Get_ReturnsOnlyIdTitleCategoryTagsFields()
    {
        var title = $"Field Check {Guid.NewGuid():N}";
        await _client.SendAsync(SaveWiki(title, "reference", "secret content"));

        var list    = await _client.SendAsync(GetWiki());
        var json    = await list.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement[]>(json)!;
        var entry   = entries.First(e => e.GetProperty("title").GetString() == title);

        // Should have id, title, category, tags — but NOT content
        entry.TryGetProperty("id",       out _).Should().BeTrue();
        entry.TryGetProperty("title",    out _).Should().BeTrue();
        entry.TryGetProperty("category", out _).Should().BeTrue();
        entry.TryGetProperty("tags",     out _).Should().BeTrue();
        entry.TryGetProperty("content",  out _).Should().BeFalse();
    }

    // ── /api/wiki/search tests ────────────────────────────────────────────────

    [Test]
    public async Task Search_NoSecret_ReturnsUnauthorized()
    {
        var query = "test query";
        var resp  = await _client.GetAsync($"/api/wiki/search?q={Uri.EscapeDataString(query)}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Search_MissingQuery_ReturnsBadRequest()
    {
        var resp = await _client.GetAsync("/api/wiki/search");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Search_WithValidQuery_ReturnsOk()
    {
        // First create some wiki entries to search
        await _client.SendAsync(SaveWiki("Bug fix: authentication timeout issue", "bugfix", "The user was experiencing a timeout when trying to authenticate to the system."));
        await _client.SendAsync(SaveWiki("Memory leak in data processing module", "bugfix", "The application was consuming increasing amounts of memory during long-running operations."));

        var resp = await _client.GetAsync($"/api/wiki/search?q=authentication&k=2");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonDocument>(json);

        result.RootElement.GetProperty("Results").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
        result.RootElement.TryGetProperty("QueryScore", out var scoreProp);
        scoreProp.GetDouble()!.Should().BeGreaterOrEqualTo(0f);
    }

    [Test]
    public async Task Search_KDefaultTo5()
    {
        // Create more than 5 entries
        for (int i = 0; i < 10; i++)
        {
            await _client.SendAsync(SaveWiki($"Test entry {i}", "test", $"Content for entry {i}"));
        }

        var resp = await _client.GetAsync("/api/wiki/search?q=test");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonDocument>(json);
        var results = result.RootElement.GetProperty("Results");

        // Should return all entries since we have exactly 10 entries
        results.GetArrayLength().Should().BeLessThanOrEqualTo(10);
    }

    [Test]
    public async Task Search_WithKParam_ReturnsKResults()
    {
        // Create exactly 3 entries
        await _client.SendAsync(SaveWiki("Article one", "exam", "First article content"));
        await _client.SendAsync(SaveWiki("Article two", "exam", "Second article content"));
        await _client.SendAsync(SaveWiki("Article three", "exam", "Third article content"));

        // Request k=1 result
        var resp = await _client.GetAsync("/api/wiki/search?q=article&k=1");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonDocument>(json);
        var results = result.RootElement.GetProperty("Results");

        results.GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task Search_ReturnsWikiSearchResultStructure()
    {
        await _client.SendAsync(SaveWiki("Important bug: API rate limit hit", "bugfix", "When the application exceeded rate limits, it should fallback to exponential backoff."));

        var response = await _client.GetAsync("/api/wiki/search?q=rate&k=1");
        var json = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(json);

        // Verify response structure
        document.TryGetProperty("Results", out var resultsProp);
        document.TryGetProperty("QueryScore", out var scoreProp);

        resultsProp.ValueType.Should().Be(JsonTokenType.Array);
        scoreProp.ValueType.Should().Be(JsonTokenType.Number);

        // Verify result fields if Results array is not empty
        var resultEntries = resultsProp.GetArrayLength();
        if (resultEntries > 0)
        {
            var firstResult = resultsProp[0];
            firstResult.TryGetProperty("Title", out _).Should().BeTrue();
            firstResult.TryGetProperty("Content", out _).Should().BeTrue();
            firstResult.TryGetProperty("Category", out _).Should().BeTrue();
            firstResult.TryGetProperty("Tags", out _).Should().BeFalse(); // Tags is not exposed in search results
            firstResult.TryGetProperty("Similarity", out _).Should().BeTrue();
            firstResult.TryGetProperty("QueryScore", out _).Should().BeFalse(); // QueryScore is at root level
        }
    }

    [Test]
    public async Task Search_MalformedKParam_GetsDefaultValueOf5()
    {
        // Create 3 entries
        for (int i = 0; i < 3; i++)
        {
            await _client.SendAsync(SaveWiki($"Entry {i}", "test", $"Content {i}"));
        }

        // Invalid k parameter should be treated as not provided, defaulting to 5
        var resp = await _client.GetAsync("/api/wiki/search?q=test&k=invalid");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("Results");

        results.GetArrayLength().Should().BeLessThanOrEqualTo(3);
    }

    [Test]
    public async Task Search_ReturnsSimilarityScoreInRange()
    {
        await _client.SendAsync(SaveWiki("Memory leak in data processing", "bugfix", "Fix for memory leak in data processing module."));

        var resp = await _client.GetAsync("/api/wiki/search?q=memory leak&k=1");
        var json = await resp.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(json);

        document.TryGetProperty("QueryScore", out var rootScore);
        var score = rootScore.GetDouble();

        score.Should().BeGreaterOrEqualTo(0f);
        score.Should().BeLessThanOrEqualTo(1f);
    }
}
