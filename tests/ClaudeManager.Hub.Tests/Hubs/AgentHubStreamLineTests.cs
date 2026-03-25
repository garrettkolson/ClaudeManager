using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Hubs;

/// <summary>
/// Tests for AgentHub.StreamLine - kind detection, truncation, and tool name extraction.
/// AgentHub is created directly (no SignalR server); StreamLine does not use Context.
/// The store is pre-seeded with a machine and session so AppendLine doesn't silently ignore.
/// </summary>
[TestFixture]
public class AgentHubStreamLineTests
{
    private SessionStore _store = default!;
    private AgentHub _hub = default!;

    [TearDown]
    public void TearDown() => _hub?.Dispose();

    [SetUp]
    public void SetUp()
    {
        var (store, _) = new SessionStoreBuilder()
            .WithQueue(Mock.Of<IPersistenceQueue>())
            .Build();
        _store = store;
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _hub = new AgentHub(_store, NullLogger<AgentHub>.Instance);
    }

    private async Task StreamAndGet(string rawJson)
    {
        await _hub.StreamLine(TestData.LineDto(rawJson));
    }

    private StreamedLine LastLine() =>
        _store.GetSession(TestData.MachineId, TestData.SessionId)!.OutputLines.Last();

    [Test]
    public async Task StreamLine_ResultTypeJson_KindIsResultSummary()
    {
        await StreamAndGet("{\"type\":\"result\",\"subtype\":\"success\"}");
        LastLine().Kind.Should().Be(StreamLineKind.ResultSummary);
    }

    [Test]
    public async Task StreamLine_AssistantTypeJson_KindIsAssistantToken()
    {
        await StreamAndGet("{\"type\":\"assistant\",\"content\":\"hello\"}");
        LastLine().Kind.Should().Be(StreamLineKind.AssistantToken);
    }

    [Test]
    public async Task StreamLine_ToolUseTypeJson_KindIsToolUse()
    {
        await StreamAndGet("{\"type\":\"tool_use\",\"name\":\"bash\"}");
        LastLine().Kind.Should().Be(StreamLineKind.ToolUse);
    }

    [Test]
    public async Task StreamLine_ToolResultTypeJson_KindIsToolResult()
    {
        await StreamAndGet("{\"type\":\"tool_result\",\"content\":\"done\"}");
        LastLine().Kind.Should().Be(StreamLineKind.ToolResult);
    }

    [Test]
    public async Task StreamLine_StderrFieldJson_KindIsError()
    {
        await StreamAndGet("{\"stderr\":\"some error\"}");
        LastLine().Kind.Should().Be(StreamLineKind.Error);
    }

    [Test]
    public async Task StreamLine_UnrecognizedJson_DefaultsToAssistantToken()
    {
        await StreamAndGet("{\"unknown\":\"value\"}");
        LastLine().Kind.Should().Be(StreamLineKind.AssistantToken);
    }

    [Test]
    public async Task StreamLine_ToolUseTypeJson_ExtractsToolName()
    {
        await StreamAndGet("{\"type\":\"tool_use\",\"name\":\"bash\"}");
        LastLine().ToolName.Should().Be("bash");
    }

    [Test]
    public async Task StreamLine_ToolUseTypeJsonMissingNameField_ToolNameIsNull()
    {
        await StreamAndGet("{\"type\":\"tool_use\"}");
        LastLine().ToolName.Should().BeNull();
    }

    [Test]
    public async Task StreamLine_ContentExactly8000Chars_NotTruncated()
    {
        var json = new string('x', 8000);
        await _hub.StreamLine(new StreamLineDto(TestData.MachineId, TestData.SessionId, json, DateTimeOffset.UtcNow));

        var line = LastLine();
        line.Content.Should().HaveLength(8000);
        line.IsContentTruncated.Should().BeFalse();
    }

    [Test]
    public async Task StreamLine_ContentExceeds8000Chars_TruncatesToFirst8000()
    {
        var json = new string('x', 9000);
        await _hub.StreamLine(new StreamLineDto(TestData.MachineId, TestData.SessionId, json, DateTimeOffset.UtcNow));

        LastLine().Content.Should().HaveLength(8000);
    }

    [Test]
    public async Task StreamLine_ContentExceeds8000Chars_SetsIsTruncatedTrue()
    {
        var json = new string('x', 9000);
        await _hub.StreamLine(new StreamLineDto(TestData.MachineId, TestData.SessionId, json, DateTimeOffset.UtcNow));

        LastLine().IsContentTruncated.Should().BeTrue();
    }

    [Test]
    public async Task StreamLine_ContentUnder8000Chars_IsTruncatedFalse()
    {
        await StreamAndGet("{\"type\":\"assistant\",\"content\":\"short\"}");
        LastLine().IsContentTruncated.Should().BeFalse();
    }
}
