namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// PLC 断线重连的等待间隔:从 1 秒开始,每失败一次翻倍,不超过上限。
/// <para>
/// 只有真正读到数据后才调用 <see cref="Reset"/>,而不是一连上就重置:
/// 遇到"能连上但读不了"的情况(DB 号写错、PLC 开了访问保护等),
/// 如果连上就重置,会变成每秒几十次地反复连接 PLC、刷满日志。
/// </para>
/// </summary>
public sealed class ReconnectBackoff
{
    /// <summary>第一次重连前的等待时间。</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _maxDelay;
    private TimeSpan _nextDelay = InitialDelay;

    /// <summary>创建退避计时,<paramref name="maxDelay"/> 为等待时间上限。</summary>
    public ReconnectBackoff(TimeSpan maxDelay)
    {
        _maxDelay = maxDelay < InitialDelay ? InitialDelay : maxDelay;
    }

    /// <summary>取出这一次应等待的时间,并把下一次的等待时间翻倍(不超过上限)。</summary>
    public TimeSpan NextDelay()
    {
        var delay = _nextDelay;
        var doubled = delay + delay;
        _nextDelay = doubled > _maxDelay ? _maxDelay : doubled;
        return delay;
    }

    /// <summary>通信恢复正常后调用,下次断线重新从 1 秒开始。</summary>
    public void Reset() => _nextDelay = InitialDelay;
}
