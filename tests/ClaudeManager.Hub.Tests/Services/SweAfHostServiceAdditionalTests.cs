using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfHostServiceAdditionalTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private SweAfHostService BuildSvc() =>
        new(_dbFactory, NullLogger<SweAfHostService>.Instance);

    // ── QuoteForShell ─────────────────────────────────────────────────────────

    [Test]
    public void QuoteForShell_PlainValue_WrapsInSingleQuotes()
    {
        SweAfHostService.QuoteForShell("myvalue")
            .Should().Be("'myvalue'");
    }

    [Test]
    public void QuoteForShell_ValueWithSingleQuote_Escapes()
    {
        // Shell-safe single-quote escaping: 'it'\''s' → it's
        SweAfHostService.QuoteForShell("it's")
            .Should().Be("'it'\\''s'");
    }

    [Test]
    public void QuoteForShell_ValueWithSpaces_WrapsCorrectly()
    {
        SweAfHostService.QuoteForShell("hello world")
            .Should().Be("'hello world'");
    }

    [Test]
    public void QuoteForShell_EmptyString_WrapsEmpty()
    {
        SweAfHostService.QuoteForShell("").Should().Be("''");
    }

    [Test]
    public void QuoteForShell_ValueWithDollarSign_NotExpanded()
    {
        // Single quotes prevent shell variable expansion — $VAR stays literal
        var result = SweAfHostService.QuoteForShell("$SECRET");
        result.Should().Be("'$SECRET'");
        result.Should().StartWith("'").And.EndWith("'");
    }

    // ── InjectEnvVars ─────────────────────────────────────────────────────────

    [Test]
    public void InjectEnvVars_NoEnvVars_ReturnsCommandUnchanged()
    {
        var host = new SweAfHostEntity { AnthropicBaseUrl = null, AnthropicApiKey = null };
        SweAfHostService.InjectEnvVars(host, "my-command.sh")
            .Should().Be("my-command.sh");
    }

    [Test]
    public void InjectEnvVars_BaseUrlOnly_PrependsVar()
    {
        var host = new SweAfHostEntity { AnthropicBaseUrl = "http://proxy:8080", AnthropicApiKey = null };
        var result = SweAfHostService.InjectEnvVars(host, "my-command.sh");
        result.Should().StartWith("ANTHROPIC_BASE_URL=");
        result.Should().Contain("http://proxy:8080");
        result.Should().EndWith("my-command.sh");
    }

    [Test]
    public void InjectEnvVars_ApiKeyOnly_PrependsVar()
    {
        var host = new SweAfHostEntity { AnthropicBaseUrl = null, AnthropicApiKey = "sk-test" };
        var result = SweAfHostService.InjectEnvVars(host, "my-command.sh");
        result.Should().Contain("ANTHROPIC_API_KEY=");
        result.Should().Contain("sk-test");
        result.Should().EndWith("my-command.sh");
    }

    [Test]
    public void InjectEnvVars_BothSet_PrependsBoth()
    {
        var host = new SweAfHostEntity
        {
            AnthropicBaseUrl = "http://proxy:8080",
            AnthropicApiKey  = "sk-test",
        };
        var result = SweAfHostService.InjectEnvVars(host, "run.sh");
        result.Should().Contain("ANTHROPIC_BASE_URL=");
        result.Should().Contain("ANTHROPIC_API_KEY=");
        result.Should().EndWith("run.sh");
    }

    [Test]
    public void InjectEnvVars_ValuesAreShellQuoted()
    {
        var host = new SweAfHostEntity { AnthropicBaseUrl = "http://proxy:8080" };
        var result = SweAfHostService.InjectEnvVars(host, "cmd");
        // Value must be single-quoted for shell safety
        result.Should().Contain("ANTHROPIC_BASE_URL='http://proxy:8080'");
    }

    [Test]
    public void InjectEnvVars_ApiKeyWithSpecialChars_QuotedCorrectly()
    {
        var host = new SweAfHostEntity { AnthropicApiKey = "key-with-$pecial" };
        var result = SweAfHostService.InjectEnvVars(host, "cmd");
        // Dollar sign inside single quotes → not expanded by shell
        result.Should().Contain("'key-with-$pecial'");
    }

    [Test]
    public void InjectEnvVars_BlankBaseUrl_NotInjected()
    {
        var host = new SweAfHostEntity { AnthropicBaseUrl = "   ", AnthropicApiKey = null };
        SweAfHostService.InjectEnvVars(host, "cmd")
            .Should().Be("cmd");
    }

    // ── BuildAuth ─────────────────────────────────────────────────────────────

    [Test]
    public void BuildAuth_NoKeyNoPassword_ReturnsNull()
    {
        var host = new SweAfHostEntity { SshKeyPath = null, SshPassword = null, SshUser = "user" };
        SweAfHostService.BuildAuth(host).Should().BeNull();
    }

    [Test]
    public void BuildAuth_PasswordSet_ReturnsPasswordAuth()
    {
        var host = new SweAfHostEntity { SshPassword = "secret", SshUser = "user" };
        var auth = SweAfHostService.BuildAuth(host);
        auth.Should().NotBeNull();
        auth.Should().BeOfType<PasswordAuthenticationMethod>();
    }

    [Test]
    public void BuildAuth_PasswordAuth_UsesCorrectUsername()
    {
        var host = new SweAfHostEntity { SshPassword = "secret", SshUser = "ubuntu" };
        var auth = SweAfHostService.BuildAuth(host) as PasswordAuthenticationMethod;
        auth!.Username.Should().Be("ubuntu");
    }

    // ── CRUD: GetByIdAsync ────────────────────────────────────────────────────

    [Test]
    public async Task GetByIdAsync_ExistingId_ReturnsEntity()
    {
        var svc = BuildSvc();
        var created = await svc.CreateAsync("Host A");

        var found = await svc.GetByIdAsync(created.Id);
        found.Should().NotBeNull();
        found!.DisplayName.Should().Be("Host A");
    }

    [Test]
    public async Task GetByIdAsync_MissingId_ReturnsNull()
    {
        var svc = BuildSvc();
        var found = await svc.GetByIdAsync(9999);
        found.Should().BeNull();
    }

    // ── CRUD: UpdateAsync ─────────────────────────────────────────────────────

    [Test]
    public async Task UpdateAsync_PersistsChanges()
    {
        var svc     = BuildSvc();
        var created = await svc.CreateAsync("Old Name");

        created.DisplayName      = "New Name";
        created.AnthropicBaseUrl = "http://new-proxy:8080";
        await svc.UpdateAsync(created);

        var updated = await svc.GetByIdAsync(created.Id);
        updated!.DisplayName.Should().Be("New Name");
        updated.AnthropicBaseUrl.Should().Be("http://new-proxy:8080");
    }

    // ── CRUD: CreateAsync (simple overload) ───────────────────────────────────

    [Test]
    public async Task CreateAsync_SimpleOverload_SetsDefaults()
    {
        var svc     = BuildSvc();
        var created = await svc.CreateAsync("Simple Host");

        created.Host.Should().Be("control-plane");
        created.SshPort.Should().Be(8080);
        created.SshUser.Should().BeNull();
        created.SshKeyPath.Should().BeNull();
        created.SshPassword.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_BlankApiKey_StoresNull()
    {
        var svc     = BuildSvc();
        var created = await svc.CreateAsync("Host", anthropicApiKey: "  ");

        created.AnthropicApiKey.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_BlankBaseUrl_StoresNull()
    {
        var svc     = BuildSvc();
        var created = await svc.CreateAsync("Host", anthropicBaseUrl: "");

        created.AnthropicBaseUrl.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_TrimsDisplayName()
    {
        var svc     = BuildSvc();
        var created = await svc.CreateAsync("  Padded Name  ");

        created.DisplayName.Should().Be("Padded Name");
    }
}
