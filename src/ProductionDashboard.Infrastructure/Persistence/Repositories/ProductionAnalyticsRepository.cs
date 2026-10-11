using Dapper;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IProductionAnalyticsRepository"/>
public sealed class ProductionAnalyticsRepository : IProductionAnalyticsRepository
{
    private readonly IDbConnectionFactory _factory;

    /// <summary>注入连接工厂(读 / 写连接由 factory 决定 readonly/journal 模式)。</summary>
    public ProductionAnalyticsRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    // ── 总数 / 范围统计 ─────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<int> CountEventsAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM ProductionEvent
            WHERE ShiftDate = @shiftDate AND ShiftDefId = @shiftDefId;
            """;

        await using var conn = _factory.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { shiftDate = shiftDate.ToString("yyyy-MM-dd"), shiftDefId },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<int> CountEventsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM ProductionEvent
            WHERE Timestamp >= @from AND Timestamp < @to;
            """;

        await using var conn = _factory.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { from = fromUtc.ToString("O"), to = toUtc.ToString("O") },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<int> CountEventsByShiftDateRangeAsync(
        DateOnly fromDate, DateOnly toDate, int? shiftDefId,
        CancellationToken cancellationToken)
    {
        // 聚合分层:周/月/年/今日总数走 ProductionMinute 分钟桶 SUM,而非直扫 ProductionEvent。
        // ProductionMinute ~50万行/年 vs ProductionEvent ~3000万行/年,大范围查询快约 60×。
        // 代价:最近未满 1 分钟尚未聚合(今日累计可能少 < 1 分钟的量);实时数字由 Hero 内存计数
        // 保证,本方法服务于 2 秒刷新的聚合视图,可接受。空产量分钟无行,SUM 自然为 0。
        var sql = shiftDefId.HasValue
            ? """
              SELECT COALESCE(SUM(Count), 0) FROM ProductionMinute
              WHERE ShiftDate >= @from AND ShiftDate <= @to AND ShiftDefId = @shiftDefId;
              """
            : """
              SELECT COALESCE(SUM(Count), 0) FROM ProductionMinute
              WHERE ShiftDate >= @from AND ShiftDate <= @to;
              """;

        await using var conn = _factory.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd"),
                shiftDefId,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<int> ComputePeakHourCountAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken)
    {
        // 按 ISO 时间戳前缀 (yyyy-MM-ddTHH) 分桶 -> 取最大值
        const string sql = """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(*) AS c
                FROM ProductionEvent
                WHERE ShiftDate = @shiftDate AND ShiftDefId = @shiftDefId
                GROUP BY substr(Timestamp, 1, 13)
            );
            """;

        await using var conn = _factory.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { shiftDate = shiftDate.ToString("yyyy-MM-dd"), shiftDefId },
            cancellationToken: cancellationToken));
    }

    // ── 时间序列 ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> GetHourlyCountsForDateAsync(
        DateOnly localDate, CancellationToken cancellationToken)
    {
        // 用本地时区的 [date 00:00, date+1 00:00) UTC 区间过滤,然后用 strftime('%H')
        // 分组到 24 个小时桶。
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue);
        var fromUtc = localMidnight.ToUniversalTime();
        var toUtc = fromUtc.AddDays(1);

        const string sql = """
            SELECT CAST(strftime('%H', Timestamp, 'localtime') AS INTEGER) AS Hour,
                   COUNT(*) AS Cnt
            FROM ProductionEvent
            WHERE Timestamp >= @from AND Timestamp < @to
            GROUP BY Hour;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<(long Hour, long Cnt)>(new CommandDefinition(
            sql,
            new { from = fromUtc.ToString("O"), to = toUtc.ToString("O") },
            cancellationToken: cancellationToken));

        var result = new int[24];
        foreach (var r in rows)
        {
            if (r.Hour is >= 0 and < 24) result[r.Hour] = (int)r.Cnt;
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DailyCount>> GetDailyCountsAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ShiftDate, COUNT(*) AS Cnt
            FROM ProductionEvent
            WHERE ShiftDate >= @from AND ShiftDate <= @to
            GROUP BY ShiftDate
            ORDER BY ShiftDate ASC;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<(string ShiftDate, long Cnt)>(new CommandDefinition(
            sql,
            new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd"),
            },
            cancellationToken: cancellationToken));

        var byDate = rows.ToDictionary(r => DateOnly.Parse(r.ShiftDate), r => (int)r.Cnt);

        // 填充无产量的日子 = 0,保证序列连续
        var result = new List<DailyCount>();
        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            result.Add(new DailyCount(d, byDate.TryGetValue(d, out var c) ? c : 0));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DailyShiftCount>> GetDailyCountsByShiftAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ShiftDate, ShiftDefId, COUNT(*) AS Cnt
            FROM ProductionEvent
            WHERE ShiftDate >= @from AND ShiftDate <= @to
            GROUP BY ShiftDate, ShiftDefId
            ORDER BY ShiftDate ASC, ShiftDefId ASC;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<(string ShiftDate, long ShiftDefId, long Cnt)>(
            new CommandDefinition(
                sql,
                new
                {
                    from = fromDate.ToString("yyyy-MM-dd"),
                    to = toDate.ToString("yyyy-MM-dd"),
                },
                cancellationToken: cancellationToken));

        return rows
            .Select(r => new DailyShiftCount(
                DateOnly.Parse(r.ShiftDate), (int)r.ShiftDefId, (int)r.Cnt))
            .ToArray();
    }

    // ── 按班组维度 ─────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<int> CountEventsByTeamRangeAsync(
        DateOnly fromDate, DateOnly toDate, int teamId,
        CancellationToken cancellationToken)
    {
        // 注意:按班组维度此处**仍走 ProductionEvent**,未切 ProductionMinute SUM。
        // 原因:ProductionMinute.TeamId 从本次起才开始填(向前),历史分钟行 TeamId 为 NULL,
        // 现在就切按班组聚合会让历史区间少算。待历史数据自然轮换(年度归档)或专门回填后再切。
        const string sql = """
            SELECT COUNT(*) FROM ProductionEvent
            WHERE ShiftDate >= @from AND ShiftDate <= @to AND TeamId = @teamId;
            """;

        await using var conn = _factory.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd"),
                teamId,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DailyTeamCount>> GetDailyCountsByTeamAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ShiftDate, TeamId, COUNT(*) AS Cnt
            FROM ProductionEvent
            WHERE ShiftDate >= @from AND ShiftDate <= @to AND TeamId IS NOT NULL
            GROUP BY ShiftDate, TeamId
            ORDER BY ShiftDate ASC, TeamId ASC;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<(string ShiftDate, long TeamId, long Cnt)>(
            new CommandDefinition(
                sql,
                new
                {
                    from = fromDate.ToString("yyyy-MM-dd"),
                    to = toDate.ToString("yyyy-MM-dd"),
                },
                cancellationToken: cancellationToken));

        return rows
            .Select(r => new DailyTeamCount(
                DateOnly.Parse(r.ShiftDate), (int)r.TeamId, (int)r.Cnt))
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task<int> CountEventsForTeamInRangeAsync(
        DateTime fromUtc, DateTime toUtc, int teamId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM ProductionEvent
            WHERE Timestamp >= @from AND Timestamp < @to AND TeamId = @teamId;
            """;

        await using var conn = _factory.OpenRead();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                from = fromUtc.ToString("O"),
                to = toUtc.ToString("O"),
                teamId,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, int>> CountEventsByTeamInRangeAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TeamId, COUNT(*) AS Cnt
            FROM ProductionEvent
            WHERE Timestamp >= @from AND Timestamp < @to AND TeamId IS NOT NULL
            GROUP BY TeamId;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<(long TeamId, long Cnt)>(new CommandDefinition(
            sql,
            new { from = fromUtc.ToString("O"), to = toUtc.ToString("O") },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(r => (int)r.TeamId, r => (int)r.Cnt);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, int>> ComputePeakHourCountsByTeamAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken)
    {
        // 对每个 TeamId 都按小时桶取最大值。
        // 内层:(TeamId, hour) → 该小时该班组事件数
        // 外层:对每个 TeamId 取 MAX(c)
        const string sql = """
            SELECT TeamId, COALESCE(MAX(c), 0) AS PeakC FROM (
                SELECT TeamId, substr(Timestamp, 1, 13) AS HourBucket, COUNT(*) AS c
                FROM ProductionEvent
                WHERE ShiftDate = @shiftDate AND ShiftDefId = @shiftDefId AND TeamId IS NOT NULL
                GROUP BY TeamId, HourBucket
            )
            GROUP BY TeamId;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<(long TeamId, long PeakC)>(new CommandDefinition(
            sql,
            new { shiftDate = shiftDate.ToString("yyyy-MM-dd"), shiftDefId },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(r => (int)r.TeamId, r => (int)r.PeakC);
    }
}
