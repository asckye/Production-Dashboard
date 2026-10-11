using Dapper;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IShiftSummaryRepository"/>
public sealed class ShiftSummaryRepository : IShiftSummaryRepository
{
    private readonly IDbConnectionFactory _factory;

    /// <summary>注入连接工厂(读 / 写连接由 factory 决定 readonly/journal 模式)。</summary>
    public ShiftSummaryRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task UpsertShiftSummaryAsync(
        ShiftSummary summary, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO ShiftSummary
                (ShiftDate, ShiftDefId, TeamId, ShiftName, StartTime, EndTime,
                 TargetCount, ActualCount, PeakHourCount, Remark)
            VALUES
                (@ShiftDate, @ShiftDefId, @TeamId, @ShiftName, @StartTime, @EndTime,
                 @TargetCount, @ActualCount, @PeakHourCount, @Remark)
            ON CONFLICT(ShiftDate, ShiftDefId, TeamId) DO UPDATE SET
                ShiftName     = excluded.ShiftName,
                StartTime     = excluded.StartTime,
                EndTime       = excluded.EndTime,
                TargetCount   = excluded.TargetCount,
                ActualCount   = excluded.ActualCount,
                PeakHourCount = excluded.PeakHourCount,
                Remark        = excluded.Remark
            -- 已结算的行(EndTime 非空)不能被"进行中"的刷新覆盖:
            -- 班次切换瞬间,周期刷新可能还拿着旧班次和新周期的计数,晚于结算写入就会把结算结果冲掉
            WHERE excluded.EndTime IS NOT NULL OR ShiftSummary.EndTime IS NULL;
            """;

        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                ShiftDate = summary.ShiftDate.ToString("yyyy-MM-dd"),
                summary.ShiftDefId,
                summary.TeamId,
                summary.ShiftName,
                StartTime = summary.StartTime.ToString("O"),
                EndTime = summary.EndTime?.ToString("O"),
                summary.TargetCount,
                summary.ActualCount,
                summary.PeakHourCount,
                summary.Remark,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<ShiftSummary?> GetShiftSummaryAsync(
        DateOnly shiftDate, int shiftDefId, int teamId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                ShiftDate, ShiftDefId, TeamId, ShiftName, StartTime, EndTime,
                TargetCount, ActualCount, PeakHourCount, Remark
            FROM ShiftSummary
            WHERE ShiftDate = @shiftDate AND ShiftDefId = @shiftDefId AND TeamId = @teamId;
            """;

        await using var conn = _factory.OpenRead();
        var row = await conn.QuerySingleOrDefaultAsync<SummaryRow>(new CommandDefinition(
            sql,
            new { shiftDate = shiftDate.ToString("yyyy-MM-dd"), shiftDefId, teamId },
            cancellationToken: cancellationToken));

        return row is null ? null : MapSummary(row);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShiftSummary>> GetShiftSummariesForShiftAsync(
        DateOnly shiftDate, int shiftDefId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                ShiftDate, ShiftDefId, TeamId, ShiftName, StartTime, EndTime,
                TargetCount, ActualCount, PeakHourCount, Remark
            FROM ShiftSummary
            WHERE ShiftDate = @shiftDate AND ShiftDefId = @shiftDefId
            ORDER BY TeamId;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            sql,
            new { shiftDate = shiftDate.ToString("yyyy-MM-dd"), shiftDefId },
            cancellationToken: cancellationToken));

        return rows.Select(MapSummary).ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShiftSummary>> GetShiftSummariesInRangeAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                ShiftDate, ShiftDefId, TeamId, ShiftName, StartTime, EndTime,
                TargetCount, ActualCount, PeakHourCount, Remark
            FROM ShiftSummary
            WHERE ShiftDate >= @from AND ShiftDate <= @to
            ORDER BY ShiftDate DESC, ShiftDefId ASC, TeamId ASC;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            sql,
            new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd"),
            },
            cancellationToken: cancellationToken));

        return rows.Select(MapSummary).ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShiftSummary>> GetUnfinishedShiftSummariesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                ShiftDate, ShiftDefId, TeamId, ShiftName, StartTime, EndTime,
                TargetCount, ActualCount, PeakHourCount, Remark
            FROM ShiftSummary
            WHERE EndTime IS NULL
            ORDER BY ShiftDate, ShiftDefId, TeamId;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(MapSummary).ToArray();
    }

    private sealed record SummaryRow(
        string ShiftDate, long ShiftDefId, long TeamId, string ShiftName,
        string StartTime, string? EndTime,
        long TargetCount, long ActualCount, long? PeakHourCount, string? Remark);

    private static ShiftSummary MapSummary(SummaryRow r) => new(
        DateOnly.Parse(r.ShiftDate),
        (int)r.ShiftDefId, (int)r.TeamId, r.ShiftName,
        DateTime.Parse(r.StartTime, null, System.Globalization.DateTimeStyles.RoundtripKind),
        string.IsNullOrEmpty(r.EndTime) ? null : DateTime.Parse(r.EndTime, null, System.Globalization.DateTimeStyles.RoundtripKind),
        (int)r.TargetCount, (int)r.ActualCount, (int?)r.PeakHourCount, r.Remark);
}
