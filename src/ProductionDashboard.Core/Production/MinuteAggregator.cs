using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Production;

/// <summary>
/// 把"上一分钟"的原始事件聚合成一行 <c>ProductionMinute</c>(带班组归属)。由调度器每分钟触发。
/// 从 AggregationHostedService 拆出,独立可测。
/// </summary>
public interface IMinuteAggregator
{
    /// <summary>聚合上一整分钟 [prevMinute, thisMinute) 的事件。内部已处理异常 + 限流告警。</summary>
    Task AggregateAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMinuteAggregator"/>
public sealed class MinuteAggregator : IMinuteAggregator
{
    private const long ReportThrottleMs = 30_000;
    private long _lastReportTick = long.MinValue;

    private readonly IProductionEventRepository _events;
    private readonly IProductionAnalyticsRepository _analytics;
    private readonly IShiftManager _shifts;
    private readonly ITeamAssignmentService _teamAssign;
    private readonly IGlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<MinuteAggregator> _logger;
    private readonly TimeProvider _clock;

    /// <summary>注入仓库 / 班次 / 班组 / 全局异常告警 / 日志。<paramref name="clock"/> 可注入测试时钟。</summary>
    public MinuteAggregator(
        IProductionEventRepository events,
        IProductionAnalyticsRepository analytics,
        IShiftManager shifts,
        ITeamAssignmentService teamAssign,
        IGlobalExceptionHandler exceptionHandler,
        ILogger<MinuteAggregator> logger,
        TimeProvider? clock = null)
    {
        _events = events;
        _analytics = analytics;
        _shifts = shifts;
        _teamAssign = teamAssign;
        _exceptionHandler = exceptionHandler;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task AggregateAsync(CancellationToken cancellationToken)
    {
        // 取上一分钟整点为窗口起点 (UTC),例如现在 12:34:17,聚合 [12:33:00, 12:34:00)。
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var thisMinute = new DateTime(
            nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        var prevMinute = thisMinute.AddMinutes(-1);
        var prevMinuteEnd = thisMinute;

        try
        {
            var count = await _analytics.CountEventsInRangeAsync(prevMinute, prevMinuteEnd, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) return;

            var prevMinuteLocal = prevMinute.ToLocalTime();
            var shift = _shifts.GetShiftAt(prevMinuteLocal);
            if (shift is null)
            {
                _logger.LogWarning(
                    "分钟聚合发现 {Count} 条事件但落在空档期 {Time:HH:mm},忽略", count, prevMinuteLocal);
                return;
            }

            // 记录该分钟所属班组(当前槽位分配)。一分钟内正常单班组,倒班跨分钟误差 < 1分钟。
            var team = _teamAssign.GetTeamForSlot(shift.Definition.Id);

            await _events.UpsertMinuteAggregateAsync(
                prevMinute, shift.ShiftDate, shift.Definition.Id, team?.Id, count, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "分钟聚合 {Time:HH:mm} · {Count} 件 · {Shift}",
                prevMinuteLocal, count, shift.Definition.Code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分钟聚合失败 · {Time}", prevMinute);
            // 持续失败会让 ProductionMinute 出现空洞(小时趋势图缺数据),限流告警。
            ReportThrottled(ex);
        }
    }

    private void ReportThrottled(Exception ex)
    {
        var now = Environment.TickCount64;
        var prev = Interlocked.Read(ref _lastReportTick);
        if (now - prev < ReportThrottleMs) return;
        if (Interlocked.CompareExchange(ref _lastReportTick, now, prev) != prev) return;
        _exceptionHandler.Report(ex, "Aggregation.Minute");
    }
}
