using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeManager.Integration.Tests;

/// <summary>
/// Verifies that data received via SignalR (agent → hub) is durably written to SQLite.
/// These tests check the full pipeline: AgentHub → SessionStore → PersistenceQueue → DB.
/// </summary>
[TestFixture]
public class PersistenceRoundTripTests
{
    private HubWebApplicationFactory _factory = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new HubWebApplicationFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentRegistration Reg(string machineId) =>
        new(machineId, $"Persist Machine {machineId[..8]}", "linux", "1.0.0");

    private IDbContextFactory<ClaudeManagerDbContext> DbFactory =>
        _factory.Services.GetRequiredService<IDbContextFactory<ClaudeManagerDbContext>>();

    /// <summary>
    /// Polls <paramref name="condition"/> up to <paramref name="timeoutMs"/> ms,
    /// checking every 50 ms. Used to wait for async DB writes to complete.
    /// </summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task StreamLine_PersistedToDb()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent",   Reg(machineId));
        await conn.InvokeAsync("SessionStarted",  machineId, sessionId, Path.GetTempPath(), "hello");
        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId,
            "{\"type\":\"assistant\",\"text\":\"Persisted!\"}",
            DateTimeOffset.UtcNow));

        // Wait for the PersistenceQueue background service to write the line
        await WaitForAsync(async () =>
        {
            await using var db = DbFactory.CreateDbContext();
            return await db.StreamedLines.AnyAsync(l => l.SessionId == sessionId);
        });

        await using var verify = DbFactory.CreateDbContext();
        var lines = await verify.StreamedLines
            .Where(l => l.SessionId == sessionId)
            .ToListAsync();

        lines.Should().HaveCount(1);
        lines[0].Kind.Should().Be(StreamLineKind.AssistantToken);
        lines[0].Content.Should().Contain("Persisted!");
    }

    [Test]
    public async Task SessionEnded_StatusPersistedToDb()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent",  Reg(machineId));
        await conn.InvokeAsync("SessionStarted", machineId, sessionId, Path.GetTempPath(), "test");
        await conn.InvokeAsync("SessionEnded",   machineId, sessionId, 0);

        // Wait for the session row to be written/updated in DB
        await WaitForAsync(async () =>
        {
            await using var db = DbFactory.CreateDbContext();
            var entity = await db.ClaudeSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == sessionId);
            return entity?.Status == SessionStatus.Ended;
        });

        await using var verify = DbFactory.CreateDbContext();
        var session = await verify.ClaudeSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        session.Should().NotBeNull();
        session!.Status.Should().Be(SessionStatus.Ended);
        session.EndedAt.Should().NotBeNull();
    }

    [Test]
    public async Task MultipleLines_AllPersistedInOrder()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent",  Reg(machineId));
        await conn.InvokeAsync("SessionStarted", machineId, sessionId, Path.GetTempPath(), "multi");

        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId, "{\"type\":\"assistant\",\"text\":\"Line 1\"}", DateTimeOffset.UtcNow));
        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId, "{\"type\":\"tool_use\",\"name\":\"Read\"}", DateTimeOffset.UtcNow));
        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId, "{\"type\":\"result\",\"subtype\":\"success\"}", DateTimeOffset.UtcNow));

        await WaitForAsync(async () =>
        {
            await using var db = DbFactory.CreateDbContext();
            return await db.StreamedLines.CountAsync(l => l.SessionId == sessionId) >= 3;
        });

        await using var verify = DbFactory.CreateDbContext();
        var lines = await verify.StreamedLines
            .Where(l => l.SessionId == sessionId)
            .OrderBy(l => l.Id)
            .ToListAsync();

        lines.Should().HaveCount(3);
        lines[0].Kind.Should().Be(StreamLineKind.AssistantToken);
        lines[1].Kind.Should().Be(StreamLineKind.ToolUse);
        lines[2].Kind.Should().Be(StreamLineKind.ResultSummary);
    }

    [Test]
    public async Task AgentRegistration_MachinePersistedToDb()
    {
        var machineId   = $"m-{Guid.NewGuid():N}";
        var displayName = $"Persist Test {machineId[..8]}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent",
            new AgentRegistration(machineId, displayName, "win32", "1.0.0"));

        await WaitForAsync(async () =>
        {
            await using var db = DbFactory.CreateDbContext();
            return await db.MachineAgents.AnyAsync(m => m.MachineId == machineId);
        });

        await using var verify = DbFactory.CreateDbContext();
        var machine = await verify.MachineAgents
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MachineId == machineId);

        machine.Should().NotBeNull();
        machine!.DisplayName.Should().Be(displayName);
        machine.Platform.Should().Be("win32");
    }
}
