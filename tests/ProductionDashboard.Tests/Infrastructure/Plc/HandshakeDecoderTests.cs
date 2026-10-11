using FluentAssertions;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Tests.Infrastructure.Plc;

/// <summary>
/// 握手协议状态机的纯函数测试。覆盖:
/// <list type="bullet">
///   <item>启动 / 重连首次读 → 只对齐序号,不计数,不清位</item>
///   <item>位 = 0 → 无事件</item>
///   <item>位 = 1 + 序号未变 → 上位机崩过的痕迹,只清位不重计</item>
///   <item>位 = 1 + 序号 +1 → delta=1,正常计数 + 清位</item>
///   <item>DWORD 回卷(uint.MaxValue → 0)→ delta 仍正确</item>
///   <item>一次轮询窗口跨多个 PLC 触发 → delta &gt; 1</item>
/// </list>
/// </summary>
public sealed class HandshakeDecoderTests
{
    [Fact]
    public void First_read_aligns_lastSeq_without_counting_or_clearing()
    {
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: true, seq: 42u, isFirstRead: true, lastSeq: 0u);

        delta.Should().Be(0, "首次读不计数,只对齐");
        shouldClear.Should().BeFalse("首次读不清位,留给下次轮询正常处理");
        newLast.Should().Be(42u);
    }

    [Fact]
    public void First_read_with_bit_zero_still_aligns_seq()
    {
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: false, seq: 100u, isFirstRead: true, lastSeq: 0u);

        delta.Should().Be(0);
        shouldClear.Should().BeFalse();
        newLast.Should().Be(100u);
    }

    [Fact]
    public void Bit_zero_means_no_pending_event()
    {
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: false, seq: 50u, isFirstRead: false, lastSeq: 50u);

        delta.Should().Be(0);
        shouldClear.Should().BeFalse();
        newLast.Should().Be(50u, "lastSeq 不变");
    }

    [Fact]
    public void Bit_set_but_seq_unchanged_only_clears_does_not_count()
    {
        // 场景:上位机已经计数了 seq=10 这次事件,但写 0 清位前崩了。
        // 重启对齐到 lastSeq=10。下次轮询读到 bit=1, seq=10 → 不能重复计数。
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: true, seq: 10u, isFirstRead: false, lastSeq: 10u);

        delta.Should().Be(0, "崩后重启读到的脏标志,不应重复计数");
        shouldClear.Should().BeTrue("清位告诉 PLC 我已处理");
        newLast.Should().Be(10u);
    }

    [Fact]
    public void Bit_set_with_seq_plus_one_counts_one_and_clears()
    {
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: true, seq: 11u, isFirstRead: false, lastSeq: 10u);

        delta.Should().Be(1);
        shouldClear.Should().BeTrue();
        newLast.Should().Be(11u);
    }

    [Theory]
    [InlineData(10u, 13u, 3)]      // 一次轮询窗口跨 3 个触发
    [InlineData(0u, 100u, 100)]    // 断线期间累计 100 个,重连后补上
    [InlineData(0u, 10000u, 10000)]  // 合理增量的上限
    public void Seq_jump_yields_delta_equal_to_difference(
        uint lastSeq, uint newSeq, int expectedDelta)
    {
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: true, seq: newSeq, isFirstRead: false, lastSeq: lastSeq);

        delta.Should().Be(expectedDelta);
        shouldClear.Should().BeTrue();
        newLast.Should().Be(newSeq);
    }

    [Theory]
    [InlineData(5000u, 0u)]          // PLC 重启,序号从 0 重新开始
    [InlineData(5000u, 3u)]          // PLC 重启后已经又生产了几件
    [InlineData(0u, 1000000u)]       // 序号被手动改写成很大的值
    public void Implausible_jump_is_treated_as_counter_reset_and_realigned(uint lastSeq, uint newSeq)
    {
        // 回归:原实现按模 2^32 计算,PLC 重启后会一次计入约 43 亿件
        var (delta, shouldClear, newLast) =
            HandshakeDecoder.Decode(bit: true, seq: newSeq, isFirstRead: false, lastSeq: lastSeq);

        HandshakeDecoder.IsCounterReset(newSeq, lastSeq).Should().BeTrue();
        delta.Should().Be(0);
        shouldClear.Should().BeTrue("应答 PLC,让它继续下一次握手");
        newLast.Should().Be(newSeq, "从新的序号重新开始计数");
    }

    [Theory]
    [InlineData(uint.MaxValue, 0u, 1)]              // 自然回卷一次
    [InlineData(uint.MaxValue - 1u, 2u, 4)]         // 跨回卷多个
    [InlineData(uint.MaxValue - 5u, uint.MaxValue, 5)]  // 临近回卷但未回卷
    public void DWord_wraparound_uses_modular_arithmetic(
        uint lastSeq, uint newSeq, int expectedDelta)
    {
        var (delta, _, newLast) =
            HandshakeDecoder.Decode(bit: true, seq: newSeq, isFirstRead: false, lastSeq: lastSeq);

        delta.Should().Be(expectedDelta);
        newLast.Should().Be(newSeq);
    }
}
