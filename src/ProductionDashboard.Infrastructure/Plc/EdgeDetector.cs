namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// Edge 模式的上升沿检测 + 去抖 — 纯逻辑,无 IO,易测。
/// <list type="bullet">
///   <item>完成位从 0 变 1(上升沿)算一件;一直保持 1 不重复计数</item>
///   <item>距上一件不足去抖时间的上升沿视为抖动,忽略</item>
///   <item>刚连上 PLC 后的第一次采样只记录位的状态,不算上升沿(见 <see cref="Reset"/>)</item>
/// </list>
/// 去抖用单调时钟(<see cref="TimeProvider.GetTimestamp"/>):系统校时让时间倒退时,
/// 用系统时间计算间隔会得到负数,之后的信号都会被当成抖动丢掉。
/// </summary>
public sealed class EdgeDetector
{
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _clock;
    private bool _lastBit;
    private bool _aligned;
    private long? _lastEdgeAt;

    /// <summary>创建检测器。<paramref name="clock"/> 可注入测试时钟。</summary>
    public EdgeDetector(TimeSpan debounce, TimeProvider? clock = null)
    {
        _debounce = debounce;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// 重新连上 PLC 时调用:下一次采样只对齐位的状态。
    /// 否则断线前位已经是 1(或断线期间被置 1 后一直保持)时,重连后会被误当成一件新产品。
    /// </summary>
    public void Reset() => _aligned = false;

    /// <summary>处理一次采样,返回这次是否检测到一件(去抖后的上升沿)。</summary>
    public bool Sample(bool bit)
    {
        if (!_aligned)
        {
            _lastBit = bit;
            _aligned = true;
            return false;
        }

        var rising = bit && !_lastBit;
        _lastBit = bit;
        if (!rising) return false;

        var now = _clock.GetTimestamp();
        if (_lastEdgeAt is { } last && _clock.GetElapsedTime(last, now) < _debounce) return false;

        _lastEdgeAt = now;
        return true;
    }
}
