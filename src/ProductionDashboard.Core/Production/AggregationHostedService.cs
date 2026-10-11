using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Production;

/// <summary>
/// 聚合调度宿主:持有两个 timer + 订阅 ShiftChanged,把三类工作分别委派给协作服务,
/// 自身只负责调度与生命周期(不含业务逻辑):
/// <list type="bullet">
///   <item>每分钟 → <see cref="IMinuteAggregator"/></item>
///   <item>每 5 秒 → <see cref="IShiftSummaryRefresher"/></item>
///   <item>班次切换 → <see cref="IShiftSettlementService"/></item>
/// </list>
/// </summary>
public sealed class AggregationHostedService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan MinuteAggregationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SummaryRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IMinuteAggregator _minuteAggregator;
    private readonly IShiftSummaryRefresher _summaryRefresher;
    private readonly IShiftSettlementService _settlement;
    private readonly IShiftManager _shifts;
    private readonly IGlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<AggregationHostedService> _logger;
    private readonly TimeProvider _clock;

    // 追踪 FireAndForget 在途任务,让 StopAsync 能等它们收尾(尤其班次结算不能被中断写一半)。
    private readonly List<Task> _pending = new();
    private readonly Lock _pendingGate = new();

    private ITimer? _minuteTimer;
    private ITimer? _summaryTimer;
    private bool _stopped;

    /// <summary>注入三个协作服务 + 班次(订阅 ShiftChanged)+ 全局异常告警 + 日志。<paramref name="clock"/> 可注入测试时钟。</summary>
    public AggregationHostedService(
        IMinuteAggregator minuteAggregator,
        IShiftSummaryRefresher summaryRefresher,
        IShiftSettlementService settlement,
        IShiftManager shifts,
        IGlobalExceptionHandler exceptionHandler,
        ILogger<AggregationHostedService> logger,
        TimeProvider? clock = null)
    {
        _minuteAggregator = minuteAggregator;
        _summaryRefresher = summaryRefresher;
        _settlement = settlement;
        _shifts = shifts;
        _exceptionHandler = exceptionHandler;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        _shifts.ShiftChanged += OnShiftChanged;
    }

    /// <summary>启动 — 起两个 timer:每分钟聚合、每 5 秒刷新 Summary。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _minuteTimer = _clock.CreateTimer(
            _ => FireAndForget(() => _minuteAggregator.AggregateAsync(CancellationToken.None)),
            state: null,
            dueTime: MinuteAggregationInterval,
            period: MinuteAggregationInterval);

        _summaryTimer = _clock.CreateTimer(
            _ => FireAndForget(() => _summaryRefresher.RefreshAsync(CancellationToken.None)),
            state: null,
            dueTime: SummaryRefreshInterval,
            period: SummaryRefreshInterval);

        _logger.LogInformation(
            "AggregationHostedService 启动 · 分钟聚合每 {Min} · 汇总刷新每 {Sec}",
            MinuteAggregationInterval, SummaryRefreshInterval);

        return Task.CompletedTask;
    }

    /// <summary>停止 — 取消订阅,dispose 两个 timer,等待在途任务收尾,幂等。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        _shifts.ShiftChanged -= OnShiftChanged;

        if (_minuteTimer is not null) await _minuteTimer.DisposeAsync().ConfigureAwait(false);
        if (_summaryTimer is not null) await _summaryTimer.DisposeAsync().ConfigureAwait(false);

        // 取消订阅 + dispose timer 后不会再有新任务进来;这里等存量在途任务收尾,
        // 避免班次结算在关闭瞬间被中断,导致 ShiftSummary 写一半 / Excel 不完整。
        Task[] pending;
        lock (_pendingGate) pending = _pending.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("等待后台聚合任务收尾超时(5s),放弃等待");
            }
            catch (OperationCanceledException) { /* host 关闭超时,放弃等待 */ }
            catch (Exception) { /* 任务内部异常已在协作服务 / FireAndForget 里记录 */ }
        }

        _logger.LogInformation("AggregationHostedService 停止");
    }

    /// <summary>异步释放 — 委派给 StopAsync。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void OnShiftChanged(ShiftAssignment? oldShift, ShiftAssignment? newShift)
    {
        if (oldShift is null) return;
        // 结算逻辑在 IShiftSettlementService;这里只负责后台触发 + 收尾等待。
        FireAndForget(() => _settlement.SettleAsync(oldShift, CancellationToken.None));
    }

    private void FireAndForget(Func<Task> taskFactory)
    {
        var task = Task.Run(async () =>
        {
            try { await taskFactory().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台聚合任务异常");
                _exceptionHandler.Report(ex, "Aggregation.Background");
            }
        });

        lock (_pendingGate) _pending.Add(task);
        // 完成后从在途列表移除(同步续延,避免竞态遗留)
        _ = task.ContinueWith(
            t => { lock (_pendingGate) _pending.Remove(t); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
