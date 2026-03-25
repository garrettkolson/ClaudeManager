using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class WikiServiceTests
{
    private SqliteConnection _conn = default!;
    private WikiService _svc = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn = conn;
        _svc  = new WikiService(factory);
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    // ── GetAllAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task GetAllAsync_EmptyDb_ReturnsEmpty()
    {
        var result = await _svc.GetAllAsync();
        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllEntries_IncludingArchived()
    {
        await _svc.CreateAsync("Active",   "note", "content", null);
        var archived = await _svc.CreateAsync("Archived", "note", "content", null);
        await _svc.SetArchivedAsync(archived.Id, true);

        var result = await _svc.GetAllAsync();
        result.Should().HaveCount(2);
    }

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_ReturnsEntryWithCorrectFields()
    {
        var entry = await _svc.CreateAsync("My Title", "decision", "My content", "auth,jwt");

        entry.Title.Should().Be("My Title");
        entry.Category.Should().Be("decision");
        entry.Content.Should().Be("My content");
        entry.Tags.Should().Be("auth,jwt");
        entry.IsArchived.Should().BeFalse();
    }

    [Test]
    public async Task CreateAsync_WhitespaceTags_StoresNull()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", "   ");
        entry.Tags.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_NullTags_StoresNull()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", null);
        entry.Tags.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_SetsTimestamps()
    {
        var before = DateTimeOffset.UtcNow;
        var entry  = await _svc.CreateAsync("T", "note", "C", null);
        var after  = DateTimeOffset.UtcNow;

        entry.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        entry.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Test]
    public async Task CreateAsync_TagsWithWhitespace_TrimsTags()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", "  auth , jwt  ");
        entry.Tags.Should().Be("auth , jwt");  // only outer whitespace trimmed
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateAsync_UpdatesAllEditableFields()
    {
        var entry = await _svc.CreateAsync("Old Title", "note", "Old content", "old");
        await _svc.UpdateAsync(entry.Id, "New Title", "decision", "New content", "new");

        var all     = await _svc.GetAllAsync();
        var updated = all.Single();
        updated.Title.Should().Be("New Title");
        updated.Category.Should().Be("decision");
        updated.Content.Should().Be("New content");
        updated.Tags.Should().Be("new");
    }

    [Test]
    public async Task UpdateAsync_AdvancesUpdatedAt()
    {
        var entry  = await _svc.CreateAsync("T", "note", "C", null);
        var before = DateTimeOffset.UtcNow;
        await _svc.UpdateAsync(entry.Id, "T2", "note", "C2", null);

        var updated = (await _svc.GetAllAsync()).Single();
        updated.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Test]
    public async Task UpdateAsync_NonExistentId_DoesNotThrow()
    {
        await _svc.Invoking(s => s.UpdateAsync(999, "T", "note", "C", null))
            .Should().NotThrowAsync();
    }

    // ── SetArchivedAsync ──────────────────────────────────────────────────────

    [Test]
    public async Task SetArchivedAsync_Archive_SetsIsArchivedTrue()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", null);
        await _svc.SetArchivedAsync(entry.Id, true);

        (await _svc.GetAllAsync()).Single().IsArchived.Should().BeTrue();
    }

    [Test]
    public async Task SetArchivedAsync_Restore_SetsIsArchivedFalse()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", null);
        await _svc.SetArchivedAsync(entry.Id, true);
        await _svc.SetArchivedAsync(entry.Id, false);

        (await _svc.GetAllAsync()).Single().IsArchived.Should().BeFalse();
    }

    [Test]
    public async Task SetArchivedAsync_NonExistentId_DoesNotThrow()
    {
        await _svc.Invoking(s => s.SetArchivedAsync(999, true))
            .Should().NotThrowAsync();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_RemovesEntry()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", null);
        await _svc.DeleteAsync(entry.Id);

        (await _svc.GetAllAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        await _svc.Invoking(s => s.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    // ── UpsertByTitleAsync ────────────────────────────────────────────────────

    [Test]
    public async Task UpsertByTitleAsync_NewTitle_CreatesEntry()
    {
        await _svc.UpsertByTitleAsync("Brand New", "note", "Content", null);

        var all = await _svc.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].Title.Should().Be("Brand New");
    }

    [Test]
    public async Task UpsertByTitleAsync_ExistingActiveTitle_UpdatesInsteadOfCreating()
    {
        await _svc.CreateAsync("My Entry", "note", "Old content", null);
        await _svc.UpsertByTitleAsync("My Entry", "decision", "New content", "tag");

        var all = await _svc.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].Content.Should().Be("New content");
        all[0].Category.Should().Be("decision");
        all[0].Tags.Should().Be("tag");
    }

    [Test]
    public async Task UpsertByTitleAsync_CaseInsensitiveTitleMatch_UpdatesEntry()
    {
        await _svc.CreateAsync("My Entry", "note", "Original", null);
        await _svc.UpsertByTitleAsync("MY ENTRY", "note", "Updated", null);

        var all = await _svc.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].Content.Should().Be("Updated");
    }

    [Test]
    public async Task UpsertByTitleAsync_ArchivedEntryWithSameTitle_CreatesNewEntry()
    {
        var entry = await _svc.CreateAsync("Reused Title", "note", "Old content", null);
        await _svc.SetArchivedAsync(entry.Id, true);

        await _svc.UpsertByTitleAsync("Reused Title", "note", "New content", null);

        var all = await _svc.GetAllAsync();
        all.Should().HaveCount(2);
        all.Count(e => !e.IsArchived).Should().Be(1);
        all.Single(e => !e.IsArchived).Content.Should().Be("New content");
    }

    // ── BuildContextSummaryAsync ──────────────────────────────────────────────

    [Test]
    public async Task BuildContextSummaryAsync_NoEntries_ReturnsNull()
    {
        var result = await _svc.BuildContextSummaryAsync();
        result.Should().BeNull();
    }

    [Test]
    public async Task BuildContextSummaryAsync_OnlyArchivedEntries_ReturnsNull()
    {
        var entry = await _svc.CreateAsync("T", "note", "C", null);
        await _svc.SetArchivedAsync(entry.Id, true);

        var result = await _svc.BuildContextSummaryAsync();
        result.Should().BeNull();
    }

    [Test]
    public async Task BuildContextSummaryAsync_ActiveEntry_ReturnsXmlBlock()
    {
        await _svc.CreateAsync("Auth overhaul", "project", "We are rewriting auth", "auth,jwt");

        var result = await _svc.BuildContextSummaryAsync();

        result.Should().StartWith("<wiki>");
        result.Should().Contain("</wiki>");
        result.Should().Contain("Auth overhaul");
        result.Should().Contain("We are rewriting auth");
    }

    [Test]
    public async Task BuildContextSummaryAsync_ExcludesArchivedEntries()
    {
        await _svc.CreateAsync("Active entry",   "note", "Active content",   null);
        var archived = await _svc.CreateAsync("Archived entry", "note", "Archived content", null);
        await _svc.SetArchivedAsync(archived.Id, true);

        var result = await _svc.BuildContextSummaryAsync();

        result.Should().Contain("Active content");
        result.Should().NotContain("Archived content");
    }

    [Test]
    public async Task BuildContextSummaryAsync_GroupsByCategory()
    {
        await _svc.CreateAsync("D1", "decision", "...", null);
        await _svc.CreateAsync("P1", "project",  "...", null);

        var result = await _svc.BuildContextSummaryAsync();

        result.Should().Contain("<decision>").And.Contain("</decision>");
        result.Should().Contain("<project>").And.Contain("</project>");
    }

    [Test]
    public async Task BuildContextSummaryAsync_XmlEscapesSpecialCharsInTitle()
    {
        await _svc.CreateAsync("Fix <broken> & \"quoted\"", "bug", "content", null);

        var result = await _svc.BuildContextSummaryAsync();

        result.Should().Contain("&lt;broken&gt;");
        result.Should().Contain("&amp;");
        result.Should().Contain("&quot;");
    }

    [Test]
    public async Task BuildContextSummaryAsync_IncludesTagAttributeWhenPresent()
    {
        await _svc.CreateAsync("T", "note", "content", "auth,jwt");

        var result = await _svc.BuildContextSummaryAsync();

        result.Should().Contain("tags=\"auth,jwt\"");
    }

    [Test]
    public async Task BuildContextSummaryAsync_NoTagAttributeWhenTagsNull()
    {
        await _svc.CreateAsync("T", "note", "content", null);

        var result = await _svc.BuildContextSummaryAsync();

        result.Should().NotContain("tags=");
    }
}
