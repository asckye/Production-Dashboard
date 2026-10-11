namespace ProductionDashboard.Core.Production;

/// <summary>
/// 生产数据的生命周期边界 — 归档 / 清理 / 数据保留策略相关。
/// 当前只有"按日期范围批量清理"一项,未来归档接口在此扩展。
/// </summary>
public interface IDataLifecycleRepository
{
    /// <summary>
    /// 清掉 [fromDate, toDate] 范围内所有 ProductionEvent / ProductionMinute / ShiftSummary。
    /// 年度归档完调用,把上一年数据从主库移除腾出空间。
    /// 返回三表合计删除行数。
    /// </summary>
    Task<int> DeleteProductionDataInRangeAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);
}
