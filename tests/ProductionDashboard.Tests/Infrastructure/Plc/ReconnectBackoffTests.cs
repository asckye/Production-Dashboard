using FluentAssertions;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Tests.Infrastructure.Plc;

/// <summary><see cref="ReconnectBackoff"/>:1 秒起步、逐次翻倍、不超过上限,恢复正常后重置。</summary>
public sealed class ReconnectBackoffTests
{
    [Fact]
    public void Delay_doubles_until_the_cap()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(10));

        var delays = Enumerable.Range(0, 6).Select(_ => backoff.NextDelay().TotalSeconds);

        delays.Should().Equal(1, 2, 4, 8, 10, 10);
    }

    [Fact]
    public void Reset_starts_again_from_one_second()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(30));
        backoff.NextDelay();
        backoff.NextDelay();

        backoff.Reset();

        backoff.NextDelay().Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Cap_below_one_second_is_raised_to_one_second()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromMilliseconds(100));

        backoff.NextDelay().Should().Be(TimeSpan.FromSeconds(1));
        backoff.NextDelay().Should().Be(TimeSpan.FromSeconds(1));
    }
}
