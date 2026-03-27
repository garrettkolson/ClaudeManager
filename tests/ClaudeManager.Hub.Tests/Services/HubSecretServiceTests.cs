using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class HubSecretServiceTests
{
    private SqliteConnection _conn = default!;
    private HubSecretService _svc  = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn = conn;
        _svc  = new HubSecretService(factory);
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    // ── GetAsync ──────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_UnknownKey_ReturnsNull()
    {
        var result = await _svc.GetAsync("NonExistentKey");
        result.Should().BeNull();
    }

    [Test]
    public async Task GetAsync_AfterSet_ReturnsValue()
    {
        await _svc.SetAsync("MyKey", "MyValue");

        var result = await _svc.GetAsync("MyKey");
        result.Should().Be("MyValue");
    }

    [Test]
    public async Task GetAsync_HuggingFaceTokenKey_UsesCorrectConstant()
    {
        HubSecretService.HuggingFaceTokenKey.Should().Be("HuggingFaceToken");
    }

    // ── SetAsync (insert) ─────────────────────────────────────────────────────

    [Test]
    public async Task SetAsync_NewKey_InsertsRecord()
    {
        await _svc.SetAsync("KeyA", "ValueA");

        (await _svc.GetAsync("KeyA")).Should().Be("ValueA");
    }

    [Test]
    public async Task SetAsync_NullValue_StoresNull()
    {
        await _svc.SetAsync("KeyA", null);

        (await _svc.GetAsync("KeyA")).Should().BeNull();
    }

    // ── SetAsync (update) ─────────────────────────────────────────────────────

    [Test]
    public async Task SetAsync_ExistingKey_UpdatesValue()
    {
        await _svc.SetAsync("Key", "First");
        await _svc.SetAsync("Key", "Second");

        (await _svc.GetAsync("Key")).Should().Be("Second");
    }

    [Test]
    public async Task SetAsync_ExistingKey_UpdateToNull_ClearsValue()
    {
        await _svc.SetAsync("Key", "HasValue");
        await _svc.SetAsync("Key", null);

        (await _svc.GetAsync("Key")).Should().BeNull();
    }

    [Test]
    public async Task SetAsync_MultipleKeys_StoredIndependently()
    {
        await _svc.SetAsync("Key1", "Value1");
        await _svc.SetAsync("Key2", "Value2");

        (await _svc.GetAsync("Key1")).Should().Be("Value1");
        (await _svc.GetAsync("Key2")).Should().Be("Value2");
    }

    // ── HuggingFace token workflow ─────────────────────────────────────────────

    [Test]
    public async Task HuggingFaceToken_SetAndGet_RoundTrips()
    {
        const string token = "hf_abc123XYZ";

        await _svc.SetAsync(HubSecretService.HuggingFaceTokenKey, token);

        (await _svc.GetAsync(HubSecretService.HuggingFaceTokenKey)).Should().Be(token);
    }

    [Test]
    public async Task HuggingFaceToken_Clear_ReturnsNull()
    {
        await _svc.SetAsync(HubSecretService.HuggingFaceTokenKey, "hf_initial");
        await _svc.SetAsync(HubSecretService.HuggingFaceTokenKey, null);

        (await _svc.GetAsync(HubSecretService.HuggingFaceTokenKey)).Should().BeNull();
    }
}
