using System.Globalization;
using Dapper;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IProductionEventRepository"/>
public sealed class ProductionEventRepository : IProductionEventRepository
{
    private readonly IDbConnectionFactory _factory;

    /// <summary>注入连接工厂(读 / 写连接由 factory 决定 readonly/journal 模式)。</summary>
    public ProductionEventRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task<int> InsertEventsAsync(
        IEnumerable<ProductionEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var list = events.ToList();
        if (list.Count == 0) return 0;

        const string sql = """
            INSERT INTO ProductionEvent (Timestamp, ShiftDate, ShiftDefId, TeamId, Source)
            VALUES (@Timestamp, @ShiftDate, @ShiftDefId, @TeamId, @Source);
            """;

        await using var conn = _factory.OpenWrite();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)
            await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                sql,
                list.Select(e => new
                {
                    Timestamp = e.Timestamp.ToString("O"),
                    ShiftDate = e.ShiftDate.ToString("yyyy-MM-dd"),
                    e.ShiftDefId,
                    e.TeamId,
                    e.Source,
                }),
                transaction: tx,
                cancellationToken: cancellationToken));
            await tx.CommitAsync(cancellationToken);
            return rows;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<int> RebuildMinuteAggregatesAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        await using var conn = _factory.OpenWrite();
        return await conn.ExecuteAsync(new CommandDefinition(
            MinuteAggregateSql.RebuildRange,
            new { from = fromUtc.ToString("O"), to = toUtc.ToString("O") },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<DateTime?> GetLatestAggregatedMinuteAsync(CancellationToken cancellationToken)
    {
        await using var conn = _factory.OpenRead();
        var latest = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT MAX(MinuteStart) FROM ProductionMinute;", cancellationToken: cancellationToken));
        return latest is null
            ? null
            : DateTime.Parse(latest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
