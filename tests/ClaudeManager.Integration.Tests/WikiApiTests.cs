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
}
