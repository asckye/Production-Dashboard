using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Production;

/// <summary>
/// 每 5 秒把 <see cref="ICountingService"/> 的内存计数(按班组分桶)刷到进行中的 <c>ShiftSummary</c>
/// (EndTime=null)。由调度器周期触发。从 AggregationHostedService 拆出。
/// </summary>
public interface IShiftSummaryRefresher
{
    /// <summary>把当前班次每个有产量班组的内存累计 upsert 到 ShiftSummary。内部已处理异常 + 限流告警。</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IShiftSummaryRefresher"/>
public sealed class ShiftSummaryRefresher : IShiftSummaryRefresher
{
    private const long ReportThrottleMs = 30_000;
    private long _lastReportTick = long.MinValue;

    private readonly IShiftManager _shifts;
    private readonly ICountingService _counter;
    private readonly IShiftSummaryRepository _summaries;
    private readonly IGlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<ShiftSummaryRefresher> _logger;

    /// <summary>注入班次 / 计数 / 汇总仓库 / 全局异常告警 / 日志。</summary>
    public ShiftSummaryRefresher(
        IShiftManager shifts,
        ICountingService counter,
        IShiftSummaryRepository summaries,
        IGlobalExceptionHandler exceptionHandler,
        ILogger<ShiftSummaryRefresher> logger)
    {
        _shifts = shifts;
        _counter = counter;
        _summaries = summaries;
        _exceptionHandler = exceptionHandler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var current = _shifts.Current;
        if (current is null) return;

        var countsByTeam = _counter.CurrentCountsByTeam;
        if (countsByTeam.Count == 0) return;

        try
        {
            // 为每个有产量的班组写一行(同一槽位多班组场景由 PK 拆开)
            foreach (var (teamId, actual) in countsByTeam)
            {
                var summary = new ShiftSummary(
                    ShiftDate: current.ShiftDate,
                    ShiftDefId: current.Definition.Id,
                    TeamId: teamId,
                    ShiftName: current.Definition.Name,
                    StartTime: current.PeriodStart,
                    EndTime: null,           // 进行中
                    TargetCount: current.Definition.TargetCount,
                    ActualCount: actual,
                    PeakHourCount: null,     // 切换时再算
                    Remark: null);

                await _summaries.UpsertShiftSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新班次汇总(按班组分行)失败");
            ReportThrottled(ex);
        }
    }

    private void ReportThrottled(Exception ex)
    {
        var now = Environment.TickCount64;
        var prev = Interlocked.Read(ref _lastReportTick);
        if (now - prev < ReportThrottleMs) return;
        if (Interlocked.CompareExchange(ref _lastReportTick, now, prev) != prev) return;
        _exceptionHandler.Report(ex, "Aggregation.Summary");
    }
}
