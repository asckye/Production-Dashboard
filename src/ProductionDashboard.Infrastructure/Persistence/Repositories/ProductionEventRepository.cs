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
    public async Task UpsertMinuteAggregateAsync(
        DateTime minuteStartUtc, DateOnly shiftDate, int shiftDefId, int? teamId, int count,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO ProductionMinute (MinuteStart, ShiftDate, ShiftDefId, TeamId, Count)
            VALUES (@MinuteStart, @ShiftDate, @ShiftDefId, @TeamId, @Count)
            ON CONFLICT(MinuteStart) DO UPDATE SET
                ShiftDate  = excluded.ShiftDate,
                ShiftDefId = excluded.ShiftDefId,
                TeamId     = excluded.TeamId,
                Count      = excluded.Count;
            """;

        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                MinuteStart = minuteStartUtc.ToString("O"),
                ShiftDate = shiftDate.ToString("yyyy-MM-dd"),
                ShiftDefId = shiftDefId,
                TeamId = teamId,
                Count = count,
            },
            cancellationToken: cancellationToken));
    }
}
