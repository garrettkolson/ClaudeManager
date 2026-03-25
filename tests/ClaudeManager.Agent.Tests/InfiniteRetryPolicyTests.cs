using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClaudeManager.Agent.Tests;

[TestFixture]
public class InfiniteRetryPolicyTests
{
    private readonly InfiniteRetryPolicy _policy = new();

    private static RetryContext Context(long attempt) =>
        new() { PreviousRetryCount = attempt, RetryReason = null, ElapsedTime = TimeSpan.Zero };

    [Test]
    public void NextRetryDelay_FirstAttempt_ReturnsZero()
    {
        _policy.NextRetryDelay(Context(0)).Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void NextRetryDelay_SecondAttempt_Returns2Seconds()
    {
        _policy.NextRetryDelay(Context(1)).Should().Be(TimeSpan.FromSeconds(2));
    }

    [Test]
    public void NextRetryDelay_ThirdAttempt_Returns5Seconds()
    {
        _policy.NextRetryDelay(Context(2)).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void NextRetryDelay_FourthAttempt_Returns15Seconds()
    {
        _policy.NextRetryDelay(Context(3)).Should().Be(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void NextRetryDelay_FifthAttempt_Returns30Seconds()
    {
        _policy.NextRetryDelay(Context(4)).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void NextRetryDelay_RetryCountBeyondTable_Returns60Seconds()
    {
        _policy.NextRetryDelay(Context(5)).Should().Be(TimeSpan.FromSeconds(60));
        _policy.NextRetryDelay(Context(100)).Should().Be(TimeSpan.FromSeconds(60));
    }
}
