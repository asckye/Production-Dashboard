namespace ProductionDashboard.Core.Production;

/// <summary>
/// 班次×班组汇总。一个 (ShiftDate, ShiftDefId) 时段内若发生倒班,
/// 会有多条 ShiftSummary(每班组各一行);PK = (ShiftDate, ShiftDefId, TeamId)。
/// <see cref="EndTime"/> 为 null 时表示尚未结算(正在进行中)。
/// </summary>
/// <param name="ShiftDate">归属日期(跨日班次按归属规则确定)。</param>
/// <param name="ShiftDefId">班次定义主键(时间槽位)。</param>
/// <param name="TeamId">班组主键 — 真正的累计统计主体。</param>
/// <param name="ShiftName">班次显示名,落库时的快照,用于历史报表保持稳定。</param>
/// <param name="StartTime">本次班次实际开始的本地时间。</param>
/// <param name="EndTime">本次班次实际结束的本地时间;为 null 表示进行中。</param>
/// <param name="TargetCount">目标产量(件) — 目前每班组一行都记录槽位的完整目标;倒班分摊后续可加权。</param>
/// <param name="ActualCount">实际产量(件) — 该班组在该班次时段内的产出。</param>
/// <param name="PeakHourCount">峰值小时产量(件);可空表示尚未聚合出来。</param>
/// <param name="Remark">备注,例如停机原因或异常说明。</param>
public sealed record ShiftSummary(
    DateOnly ShiftDate,
    int ShiftDefId,
    int TeamId,
    string ShiftName,
    DateTime StartTime,
    DateTime? EndTime,
    int TargetCount,
    int ActualCount,
    int? PeakHourCount,
    string? Remark);
