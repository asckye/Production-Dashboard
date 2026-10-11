namespace ProductionDashboard.Infrastructure.Persistence;

/// <summary>
/// 从原始事件(ProductionEvent)计算分钟聚合(ProductionMinute)的 SQL,运行时聚合与数据库迁移共用一份。
/// <para>
/// 事件时间戳存的是 UTC 的 "O" 格式(如 <c>2026-05-19T02:05:17.1234567Z</c>),
/// 前 16 个字符就是所在分钟 <c>2026-05-19T02:05</c>,补上 <c>:00.0000000Z</c> 即与 MinuteStart 的格式一致。
/// </para>
/// </summary>
internal static class MinuteAggregateSql
{
    private const string Body = """
        INSERT INTO ProductionMinute (MinuteStart, ShiftDate, ShiftDefId, TeamId, Count)
        SELECT substr(Timestamp, 1, 16) || ':00.0000000Z',
               MAX(ShiftDate), MAX(ShiftDefId), MAX(TeamId), COUNT(*)
        FROM ProductionEvent
        WHERE {0}
        GROUP BY substr(Timestamp, 1, 16)
        ON CONFLICT(MinuteStart) DO UPDATE SET
            ShiftDate  = excluded.ShiftDate,
            ShiftDefId = excluded.ShiftDefId,
            TeamId     = excluded.TeamId,
            Count      = excluded.Count;
        """;

    /// <summary>重算 [@from, @to) 区间内的分钟聚合。</summary>
    public static readonly string RebuildRange = string.Format(
        System.Globalization.CultureInfo.InvariantCulture, Body, "Timestamp >= @from AND Timestamp < @to");

    /// <summary>用全部事件重算分钟聚合(迁移时补全历史空洞)。</summary>
    public static readonly string RebuildAll = string.Format(
        System.Globalization.CultureInfo.InvariantCulture, Body, "Timestamp IS NOT NULL");
}
