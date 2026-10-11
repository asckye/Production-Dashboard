
namespace ProductionDashboard.Core.Production;

/// <summary>
/// 生产事件写入边界。<see cref="ProductionEvent"/> 是原始事件,
/// <see cref="RebuildMinuteAggregatesAsync"/> 写的是分钟级聚合(<c>ProductionMinute</c> 表)。
/// 这两条写路径同源于生产数据,因此放在同一接口。
/// 查询从这里完全分离 — 见 <see cref="IProductionAnalyticsRepository"/>。
/// </summary>
public interface IProductionEventRepository
{
    /// <summary>批量写入事件(单事务)。返回写入的行数。</summary>
    Task<int> InsertEventsAsync(
        IEnumerable<ProductionEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// 用 [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) 内的原始事件重新计算分钟聚合,按分钟 upsert
    /// (PK = MinuteStart),返回写入(新增或更新)的分钟数。
    /// <para>
    /// 结果只取决于原始事件,同一区间重复计算是安全的,所以调用方可以放心多算几分钟来补漏。
    /// 班次 / 班组归属直接取自事件本身;一分钟内正常只有一个在产班组,
    /// 倒班恰好发生在这一分钟内时取其中一个,误差 &lt; 1 分钟。
    /// </para>
    /// </summary>
    Task<int> RebuildMinuteAggregatesAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>已聚合的最新一分钟的起点(UTC);还没有任何分钟聚合时返回 null。</summary>
    Task<DateTime?> GetLatestAggregatedMinuteAsync(CancellationToken cancellationToken);
}
