using FluentAssertions;
using ProductionDashboard.Infrastructure.Plc;
using ProductionDashboard.Tests.Helpers;

namespace ProductionDashboard.Tests.Infrastructure.Plc;

/// <summary><see cref="EdgeDetector"/>:上升沿计数、去抖、重连后不误计。</summary>
public sealed class EdgeDetectorTests
{
    private readonly ControlledTimeProvider _clock = new();

    private EdgeDetector Aligned(bool initialBit = false)
    {
        var edge = new EdgeDetector(TimeSpan.FromMilliseconds(200), _clock);
        edge.Sample(initialBit);   // 第一次采样只对齐
        return edge;
    }

    [Fact]
    public void Rising_edge_counts_once_and_a_held_bit_does_not_count_again()
    {
        var edge = Aligned();

        edge.Sample(true).Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(1));
        edge.Sample(true).Should().BeFalse("位一直保持 1,不是新的上升沿");
        edge.Sample(false).Should().BeFalse();
    }

    [Fact]
    public void Bounce_within_the_debounce_time_is_ignored()
    {
        var edge = Aligned();
        edge.Sample(true);
        edge.Sample(false);

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        edge.Sample(true).Should().BeFalse("100ms 内的第二个上升沿是抖动");

        edge.Sample(false);
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        edge.Sample(true).Should().BeTrue();
    }

    [Fact]
    public void First_sample_after_reconnect_only_aligns_even_if_the_bit_is_high()
    {
        // 回归:原实现重连时把"上一次的位"当成 0,PLC 上位一直是 1 的话会多计一件
        var edge = Aligned();
        _clock.Advance(TimeSpan.FromSeconds(5));

        edge.Reset();

        edge.Sample(true).Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));
        edge.Sample(true).Should().BeFalse();
    }

    [Fact]
    public void A_new_part_after_reconnect_still_counts()
    {
        var edge = Aligned();
        edge.Reset();
        edge.Sample(false);

        _clock.Advance(TimeSpan.FromSeconds(1));
        edge.Sample(true).Should().BeTrue();
    }
}
