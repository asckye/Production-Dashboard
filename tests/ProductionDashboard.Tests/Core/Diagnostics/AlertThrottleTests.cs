using FluentAssertions;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Tests.Helpers;

namespace ProductionDashboard.Tests.Core.Diagnostics;

/// <summary><see cref="AlertThrottle"/>:同一类故障在间隔内只放行一次,第一次总是放行。</summary>
public sealed class AlertThrottleTests
{
    private readonly ControlledTimeProvider _clock = new();

    [Fact]
    public void First_report_is_always_allowed()
    {
        // 回归:原实现用 long.MinValue 当"从未上报",相减溢出成负数,导致告警永远发不出去
        new AlertThrottle(TimeSpan.FromSeconds(30), _clock).ShouldReport().Should().BeTrue();
    }

    [Fact]
    public void Reports_within_the_interval_are_suppressed()
    {
        var throttle = new AlertThrottle(TimeSpan.FromSeconds(30), _clock);
        throttle.ShouldReport();

        _clock.Advance(TimeSpan.FromSeconds(29));

        throttle.ShouldReport().Should().BeFalse();
    }

    [Fact]
    public void Report_is_allowed_again_after_the_interval()
    {
        var throttle = new AlertThrottle(TimeSpan.FromSeconds(30), _clock);
        throttle.ShouldReport();

        _clock.Advance(TimeSpan.FromSeconds(30));

        throttle.ShouldReport().Should().BeTrue();
        throttle.ShouldReport().Should().BeFalse("刚放行过一次,重新开始计时");
    }

    [Fact]
    public void Only_one_of_many_concurrent_callers_is_allowed()
    {
        var throttle = new AlertThrottle(TimeSpan.FromSeconds(30), _clock);

        var allowed = 0;
        Parallel.For(0, 64, _ =>
        {
            if (throttle.ShouldReport()) Interlocked.Increment(ref allowed);
        });

        allowed.Should().Be(1);
    }
}
