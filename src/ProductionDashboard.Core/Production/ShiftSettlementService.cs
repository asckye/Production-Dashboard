using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Export;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Production;

/// <inheritdoc cref="IShiftSettlementService"/>
public sealed class ShiftSettlementService : IShiftSettlementService
{
    private readonly IShiftSummaryRepository _summaries;
    private readonly IProductionAnalyticsRepository _analytics;
    private readonly IExcelExportService _excel;
    private readonly IGlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<ShiftSettlementService> _logger;

    /// <summary>注入汇总仓库 / 分析仓库 / Excel 导出 / 全局异常告警 / 日志。</summary>
    public ShiftSettlementService(
        IShiftSummaryRepository summaries,
        IProductionAnalyticsRepository analytics,
        IExcelExportService excel,
        IGlobalExceptionHandler exceptionHandler,
        ILogger<ShiftSettlementService> logger)
    {
        _summaries = summaries;
        _analytics = analytics;
        _excel = excel;
        _exceptionHandler = exceptionHandler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SettleAsync(ShiftAssignment shift, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shift);

        try
        {
            // 1) 取该班次时段内每个班组的事件数
            var fromUtc = shift.PeriodStart.ToUniversalTime();
            var toUtc = shift.PeriodEnd.ToUniversalTime();
            var actualByTeam = await _analytics
                .CountEventsByTeamInRangeAsync(fromUtc, toUtc, cancellationToken)
                .ConfigureAwait(false);

            if (actualByTeam.Count == 0)
            {
                _logger.LogInformation(
                    "班次结算 · {Shift} @ {Date} · 无任何班组产出,跳过 ShiftSummary 写入",
                    shift.Definition.Code, shift.ShiftDate);
            }

            // 2) 一次性算出每个班组的峰值小时
            var peakByTeam = await _analytics
                .ComputePeakHourCountsByTeamAsync(
                    shift.ShiftDate, shift.Definition.Id, cancellationToken)
                .ConfigureAwait(false);

            // 3) 写多行 ShiftSummary
            var summaries = new List<ShiftSummary>(actualByTeam.Count);
            foreach (var (teamId, actual) in actualByTeam)
            {
                peakByTeam.TryGetValue(teamId, out var peak);
                var summary = new ShiftSummary(
                    ShiftDate: shift.ShiftDate,
                    ShiftDefId: shift.Definition.Id,
                    TeamId: teamId,
                    ShiftName: shift.Definition.Name,
                    StartTime: shift.PeriodStart,
                    EndTime: shift.PeriodEnd,
                    TargetCount: shift.Definition.TargetCount,
                    ActualCount: actual,
                    PeakHourCount: peak == 0 ? null : peak,
                    Remark: null);
                await _summaries.UpsertShiftSummaryAsync(summary, cancellationToken)
                    .ConfigureAwait(false);
                summaries.Add(summary);
            }

            _logger.LogInformation(
                "班次结算 · {Shift} @ {Date} · {Count} 个班组各自写入 · 总产 {Total} 件",
                shift.Definition.Code, shift.ShiftDate,
                summaries.Count, summaries.Sum(s => s.ActualCount));

            // 4) 班次结束自动导出 Excel(失败不影响结算流程)
            //    Excel 接口现仍是单 ShiftSummary 入参 — 汇总成 1 行总数 + 24h 数据,
            //    多班组场景在 Excel 里以 Remark 备注哪些班组共同贡献。后续可改成多 sheet。
            try
            {
                var hourly = await _analytics.GetHourlyCountsForDateAsync(
                    shift.ShiftDate, cancellationToken).ConfigureAwait(false);

                if (summaries.Count > 0)
                {
                    var combinedActual = summaries.Sum(s => s.ActualCount);
                    var combinedPeak = summaries.Max(s => s.PeakHourCount ?? 0);
                    var teamLabels = string.Join(", ",
                        summaries.Select(s => $"团队Id={s.TeamId}({s.ActualCount})"));
                    var combined = summaries[0] with
                    {
                        TeamId = 0,         // 0 表示"该班次所有班组合并"
                        ActualCount = combinedActual,
                        PeakHourCount = combinedPeak == 0 ? null : combinedPeak,
                        Remark = $"班组分布: {teamLabels}",
                    };
                    await _excel.ExportShiftAsync(combined, hourly, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exEx)
            {
                _logger.LogError(exEx,
                    "班次 Excel 导出失败(不影响结算) · {Shift} @ {Date}",
                    shift.Definition.Code, shift.ShiftDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "班次结算失败 · {Shift} @ {Date}",
                shift.Definition.Code, shift.ShiftDate);
            // 结算每班只触发一次,失败 = 该班次最终 ShiftSummary 永久缺失(年度归档也会少这条)。
            // 低频且后果严重,直接告警 UI,不限流。
            _exceptionHandler.Report(ex,
                $"班次结算失败 {shift.Definition.Code}@{shift.ShiftDate:yyyy-MM-dd},该班次汇总未写入");
        }
    }
}
