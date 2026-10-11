namespace ProductionDashboard.Core.Statistics;

/// <summary>
/// 大屏统计数字的唯一来源:查询数据库、套用目标规则,返回计算好的结果。
/// 界面层(ViewModel)只调用这个服务,不直接访问数据仓库。
/// </summary>
public interface IProductionStatisticsService
{
    /// <summary>计算 5 个维度卡、班组累计对比与趋势小柱图所需的数字。</summary>
    Task<DashboardStatistics> GetDashboardStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>计算白班 / 夜班卡片的周期产量与最近 5 分钟节拍。</summary>
    Task<ShiftCardStatistics> GetShiftCardStatisticsAsync(CancellationToken cancellationToken);
}
