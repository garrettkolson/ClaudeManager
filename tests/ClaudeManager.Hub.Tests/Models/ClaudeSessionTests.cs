using ClaudeManager.Hub.Models;
using ClaudeManager.Shared.Dto;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Models;

[TestFixture]
public class ClaudeSessionTests
{
    private static ClaudeSession MakeSession() => new()
    {
        SessionId        = "sess-1",
        MachineId        = "machine-1",
        WorkingDirectory = "/tmp",
        StartedAt        = DateTimeOffset.UtcNow,
    };

    private static StreamedLine MakeLine(int i = 0) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        SessionId = "sess-1",
        Kind      = StreamLineKind.AssistantToken,
        Content   = $"line-{i}",
    };

    [Test]
    public void AppendLine_WhenUnder2000Lines_AddsLine()
    {
        var session = MakeSession();
        session.AppendLine(MakeLine());
        session.OutputLines.Should().HaveCount(1);
    }

    [Test]
    public void AppendLine_WhenAt2000Lines_DropsOldestAndAddsNew()
    {
        var session = MakeSession();
        for (int i = 0; i < 2000; i++)
            session.AppendLine(MakeLine(i));

        var newest = new StreamedLine { Content = "newest", Kind = StreamLineKind.AssistantToken, SessionId = "sess-1", Timestamp = DateTimeOffset.UtcNow };
        session.AppendLine(newest);

        session.OutputLines.Should().HaveCount(2000);
        session.OutputLines.Last().Content.Should().Be("newest");
        session.OutputLines.First().Content.Should().Be("line-1");
    }

    [Test]
    public async Task AppendLine_ConcurrentWriters_NeverExceedsCap()
    {
        var session = MakeSession();
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 300; i++)
                    session.AppendLine(MakeLine(i));
            }));

        await Task.WhenAll(tasks);

        session.OutputLines.Count.Should().BeLessThanOrEqualTo(2000);
    }

    [Test]
    public async Task AppendLine_ConcurrentWriters_NeverThrows()
    {
        var session = MakeSession();
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 300; i++)
                    session.AppendLine(MakeLine(i));
            }));

        await FluentActions.Awaiting(() => Task.WhenAll(tasks))
            .Should().NotThrowAsync();
    }

    [Test]
    public void OutputLines_ReturnsSnapshotNotLiveList()
    {
        var session = MakeSession();
        session.AppendLine(MakeLine(0));

        var snapshot = session.OutputLines;
        session.AppendLine(MakeLine(1));

        snapshot.Should().HaveCount(1);
    }

    [Test]
    public void SetOutputLines_ReplacesExistingBuffer()
    {
        var session = MakeSession();
        session.AppendLine(MakeLine(0));

        var replacement = new[] { MakeLine(10), MakeLine(11) };
        session.SetOutputLines(replacement);

        session.OutputLines.Should().HaveCount(2);
        session.OutputLines[0].Content.Should().Be("line-10");
    }

    [Test]
    public void SetOutputLines_WithMoreThan2000_StoresAll()
    {
        var session = MakeSession();
        var lines = Enumerable.Range(0, 2500).Select(MakeLine).ToList();
        session.SetOutputLines(lines);

        session.OutputLines.Should().HaveCount(2500);
    }
}
