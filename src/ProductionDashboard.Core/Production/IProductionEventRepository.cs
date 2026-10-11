
namespace ProductionDashboard.Core.Production;

/// <summary>
/// 生产事件写入边界。<see cref="ProductionEvent"/> 是原始事件,
/// <see cref="UpsertMinuteAggregateAsync"/> 写的是分钟级聚合(<c>ProductionMinute</c> 表)。
/// 这两条写路径同源于生产数据,因此放在同一接口。
/// 查询从这里完全分离 — 见 <see cref="IProductionAnalyticsRepository"/>。
/// </summary>
public interface IProductionEventRepository
{
    /// <summary>批量写入事件(单事务)。返回写入的行数。</summary>
    Task<int> InsertEventsAsync(
        IEnumerable<ProductionEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// 插入或更新分钟聚合行(PK = MinuteStart)。<paramref name="teamId"/> 记录该分钟所属班组
    /// (一分钟内正常只有一个在产班组;倒班跨分钟的极少数情况按当前班组归属,误差 &lt; 1 分钟)。
    /// 填它让 ProductionMinute 能按班组查询、激活 IX_Minute_Team 索引。
    /// </summary>
    Task UpsertMinuteAggregateAsync(
        DateTime minuteStartUtc, DateOnly shiftDate, int shiftDefId, int? teamId, int count,
        CancellationToken cancellationToken);
}
