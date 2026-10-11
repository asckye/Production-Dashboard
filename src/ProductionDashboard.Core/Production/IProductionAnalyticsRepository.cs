namespace ProductionDashboard.Core.Production;

/// <summary>
/// 生产数据的只读聚合查询边界。所有 Count / Hourly / Daily / Peak 类查询从这里走。
/// Dashboard 的实时 5 维度卡 / 累计对比 / mini 柱图 / 班次结算的峰值小时全部来自这里。
/// 不暴露任何写操作 — 写路径走 <see cref="IProductionEventRepository"/> /
/// <see cref="IShiftSummaryRepository"/>。
/// </summary>
public interface IProductionAnalyticsRepository
{
    // ── 总数 / 范围统计 ─────────────────────────────────────────────

    /// <summary>统计指定班次的事件数。</summary>
    Task<int> CountEventsAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken);

    /// <summary>统计 UTC 时间窗 [fromUtc, toUtc) 内的事件总数。</summary>
    Task<int> CountEventsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>
    /// 按 ShiftDate 范围 [fromDate, toDate] 统计事件总数 (闭区间)。
    /// <paramref name="shiftDefId"/> 为 null 时跨所有班次,否则只算指定班次。
    /// 用来算日/周/月/年汇总。
    /// </summary>
    Task<int> CountEventsByShiftDateRangeAsync(
        DateOnly fromDate, DateOnly toDate, int? shiftDefId,
        CancellationToken cancellationToken);

    /// <summary>计算某班次最高单小时产量(用于 ShiftSummary.PeakHourCount)。</summary>
    Task<int> ComputePeakHourCountAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken);

    // ── 时间序列 ────────────────────────────────────────────────────

    /// <summary>
    /// 取指定日期(本地)的小时桶产量,返回 24 个 int。
    /// 没有产量的小时为 0。
    /// </summary>
    Task<IReadOnlyList<int>> GetHourlyCountsForDateAsync(
        DateOnly localDate, CancellationToken cancellationToken);

    /// <summary>取 [fromDate, toDate] 每日 ShiftDate 总产量序列,按日期升序。</summary>
    Task<IReadOnlyList<DailyCount>> GetDailyCountsAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    /// <summary>
    /// 取 [fromDate, toDate] 每日 × 每班 总产量。
    /// 用来给 Dashboard 7 天 A/B 双柱图喂数据。
    /// 没有产量的 (日期, 班次) 组合不返回 — 调用方自己填 0。
    /// </summary>
    Task<IReadOnlyList<DailyShiftCount>> GetDailyCountsByShiftAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    // ── 按班组维度 ─────────────────────────────────────────

    /// <summary>
    /// 统计 [fromDate, toDate] 范围内指定班组的事件总数(闭区间)。
    /// 用于:某班组本周 / 本月 / 本年累计 — 跨槽位累计,不受倒班影响。
    /// </summary>
    Task<int> CountEventsByTeamRangeAsync(
        DateOnly fromDate, DateOnly toDate, int teamId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 取 [fromDate, toDate] 每日 × 每班组 总产量。
    /// 给"7 天 甲vs乙 双柱图"喂数据(替代旧的 GetDailyCountsByShiftAsync)。
    /// 没有产量的 (日期, 班组) 组合不返回 — 调用方自己填 0。
    /// </summary>
    Task<IReadOnlyList<DailyTeamCount>> GetDailyCountsByTeamAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    /// <summary>
    /// 统计某个班组在当前班次周期内的事件数 — 给"本班次当前班组累计"用。
    /// 时间窗采用 UTC 时间戳 [fromUtc, toUtc)。
    /// </summary>
    Task<int> CountEventsForTeamInRangeAsync(
        DateTime fromUtc, DateTime toUtc, int teamId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 取 UTC 时间窗 [fromUtc, toUtc) 内,按 TeamId 分组的事件数。
    /// CountingService 启动期 / 班次切换时复盘"当前班次每班组已累计"用。
    /// 没有事件的班组不出现在结果中,调用方自己默认为 0。
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> CountEventsByTeamInRangeAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>
    /// 取 (ShiftDate, ShiftDefId) 时段内每个班组的峰值小时产量。
    /// 班次结算时一次拉完,而不是为每个班组单独跑查询。
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> ComputePeakHourCountsByTeamAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken);
}

/// <summary>某一天的总产量。</summary>
public sealed record DailyCount(DateOnly Date, int Count);

/// <summary>某一天 × 某个班次的产量。</summary>
public sealed record DailyShiftCount(DateOnly Date, int ShiftDefId, int Count);

/// <summary>某一天 × 某个班组的产量。</summary>
public sealed record DailyTeamCount(DateOnly Date, int TeamId, int Count);
