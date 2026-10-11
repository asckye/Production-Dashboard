namespace ProductionDashboard.Tests.Helpers;

/// <summary>
/// 简易可控 TimeProvider:支持 Advance() 推进时钟,并立即触发所有过期的 timer 回调。
/// 用于测试空闲超时、调度逻辑等时间敏感行为,避免真实 sleep。
/// </summary>
public sealed class ControlledTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private readonly List<ControlledTimer> _timers = new();
    private readonly Lock _gate = new();

    public ControlledTimeProvider()
    {
        _utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(this, callback, state, dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>把虚拟时钟向前推 <paramref name="span"/>,并依次执行期间到点的所有 timer 回调。</summary>
    public void Advance(TimeSpan span)
    {
        var target = _utcNow + span;
        while (true)
        {
            ControlledTimer? next;
            DateTimeOffset nextFire;
            lock (_gate)
            {
                next = _timers
                    .Where(t => t.NextFire <= target && t.NextFire > _utcNow)
                    .OrderBy(t => t.NextFire)
                    .FirstOrDefault();
                if (next is null) { _utcNow = target; break; }
                nextFire = next.NextFire;
                _utcNow = nextFire;
            }
            next.Fire();
        }
    }

    internal void RemoveTimer(ControlledTimer t) { lock (_gate) _timers.Remove(t); }

    internal sealed class ControlledTimer : ITimer
    {
        private readonly ControlledTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        public DateTimeOffset NextFire { get; private set; }
        private bool _disposed;

        public ControlledTimer(ControlledTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            NextFire = owner.GetUtcNow() + dueTime;
        }

        public void Fire()
        {
            if (_disposed) return;
            _callback(_state);
            NextFire = _period == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : NextFire + _period;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            NextFire = _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
