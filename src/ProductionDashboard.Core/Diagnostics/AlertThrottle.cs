namespace ProductionDashboard.Core.Diagnostics;

/// <summary>
/// 告警限流:同一类故障在 <c>interval</c> 内只放行一次,避免持续性故障(磁盘满、数据库被锁)刷爆界面告警。
/// <list type="bullet">
///   <item>第一次调用总是放行</item>
///   <item>用单调时钟(<see cref="TimeProvider.GetTimestamp"/>),不受系统时间调整影响</item>
///   <item>多个线程同时失败时,CAS 抢占保证只放行一个</item>
/// </list>
/// </summary>
/// <example>
/// <code>
/// private readonly AlertThrottle _drainAlerts = new(TimeSpan.FromSeconds(30));
/// ...
/// if (_drainAlerts.ShouldReport()) _exceptionHandler.Report(ex, "CountingService.Drain");
/// </code>
/// </example>
public sealed class AlertThrottle
{
    private const long NeverReported = long.MinValue;

    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private long _lastReportedAt = NeverReported;

    /// <summary>创建限流器。<paramref name="clock"/> 可注入测试时钟。</summary>
    public AlertThrottle(TimeSpan interval, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _interval = interval;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>这次是否应该上报。返回 true 时由调用方负责上报。</summary>
    public bool ShouldReport()
    {
        var now = _clock.GetTimestamp();
        var last = Interlocked.Read(ref _lastReportedAt);

        // 注意要先判断"从未上报":拿哨兵值直接相减会溢出成负数,导致永远不放行
        if (last != NeverReported && _clock.GetElapsedTime(last, now) < _interval) return false;

        return Interlocked.CompareExchange(ref _lastReportedAt, now, last) == last;
    }
}
