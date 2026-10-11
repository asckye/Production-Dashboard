
namespace ProductionDashboard.Core.Production;

/// <summary>
/// 班次汇总(<see cref="ShiftSummary"/>)CRUD 边界。
/// 班次结算 / 实时刷新 / 历史查询都从这里走。
/// </summary>
public interface IShiftSummaryRepository
{
    /// <summary>插入或更新班次汇总(主键 ShiftDate + ShiftDefId + TeamId)。</summary>
    Task UpsertShiftSummaryAsync(
        ShiftSummary summary, CancellationToken cancellationToken);

    /// <summary>读取单个班次×班组汇总,不存在返回 null。</summary>
    Task<ShiftSummary?> GetShiftSummaryAsync(
        DateOnly shiftDate, int shiftDefId, int teamId, CancellationToken cancellationToken);

    /// <summary>
    /// 读取指定 (ShiftDate, ShiftDefId) 时段内所有班组的汇总(0..N 行)。
    /// 倒班后一个槽位周期会有多个班组各占一段。
    /// </summary>
    Task<IReadOnlyList<ShiftSummary>> GetShiftSummariesForShiftAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken);

    /// <summary>
    /// 列出 [fromDate, toDate] 范围内所有 ShiftSummary,
    /// 按 ShiftDate 倒序、ShiftDefId 升序。
    /// </summary>
    Task<IReadOnlyList<ShiftSummary>> GetShiftSummariesInRangeAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    /// <summary>还没结算的汇总行(EndTime 为空)。启动时用来补结算停机期间结束的班次。</summary>
    Task<IReadOnlyList<ShiftSummary>> GetUnfinishedShiftSummariesAsync(CancellationToken cancellationToken);
}
