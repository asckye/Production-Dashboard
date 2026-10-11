using Dapper;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IShiftDefinitionRepository"/>
public sealed class ShiftDefinitionRepository : IShiftDefinitionRepository
{
    private readonly IDbConnectionFactory _factory;

    /// <summary>注入连接工厂(读 / 写连接由 factory 决定 readonly/journal 模式)。</summary>
    public ShiftDefinitionRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShiftDefinition>> GetActiveShiftDefinitionsAsync(
        DateOnly asOf, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                Id, Code, Name, StartTime, EndTime,
                CrossesMidnight, TargetCount, ColorHex, DisplayOrder,
                EffectiveFrom, EffectiveTo, CreatedAt
            FROM ShiftDefinition
            WHERE EffectiveFrom <= @asOf
              AND (EffectiveTo IS NULL OR EffectiveTo >= @asOf)
            ORDER BY DisplayOrder;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<ShiftRow>(
            new CommandDefinition(sql, new { asOf = asOf.ToString("yyyy-MM-dd") }, cancellationToken: cancellationToken));

        return rows.Select(MapShift).ToArray();
    }

    /// <inheritdoc/>
    public async Task<ShiftDefinition?> GetShiftDefinitionAsync(
        int id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                Id, Code, Name, StartTime, EndTime,
                CrossesMidnight, TargetCount, ColorHex, DisplayOrder,
                EffectiveFrom, EffectiveTo, CreatedAt
            FROM ShiftDefinition WHERE Id = @id;
            """;

        await using var conn = _factory.OpenRead();
        var row = await conn.QuerySingleOrDefaultAsync<ShiftRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));

        return row is null ? null : MapShift(row);
    }

    private const string InsertSql = """
        INSERT INTO ShiftDefinition
            (Code, Name, StartTime, EndTime, CrossesMidnight,
             TargetCount, ColorHex, DisplayOrder,
             EffectiveFrom, EffectiveTo, CreatedAt)
        VALUES
            (@Code, @Name, @StartTime, @EndTime, @CrossesMidnight,
             @TargetCount, @ColorHex, @DisplayOrder,
             @EffectiveFrom, @EffectiveTo, @CreatedAt);
        SELECT last_insert_rowid();
        """;

    private const string UpdateSql = """
        UPDATE ShiftDefinition SET
            Name            = @Name,
            StartTime       = @StartTime,
            EndTime         = @EndTime,
            CrossesMidnight = @CrossesMidnight,
            TargetCount     = @TargetCount,
            ColorHex        = @ColorHex,
            DisplayOrder    = @DisplayOrder
        WHERE Id = @Id;
        """;

    private const string SoftDeleteSql = """
        UPDATE ShiftDefinition SET EffectiveTo = @effectiveTo WHERE Id = @id;
        DELETE FROM ShiftSlotAssignment WHERE ShiftDefId = @id;
        """;

    private const string AssignSql = """
        INSERT INTO ShiftSlotAssignment (ShiftDefId, TeamId, AssignedAt, AssignedByOperator)
        VALUES (@ShiftDefId, @TeamId, @AssignedAt, @AssignedByOperator)
        ON CONFLICT(ShiftDefId) DO UPDATE SET
            TeamId             = excluded.TeamId,
            AssignedAt         = excluded.AssignedAt,
            AssignedByOperator = excluded.AssignedByOperator;
        """;

    /// <inheritdoc/>
    public async Task<int> InsertShiftDefinitionAsync(
        ShiftDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await using var conn = _factory.OpenWrite();
        var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            InsertSql, ToInsertParameters(definition), cancellationToken: cancellationToken));
        return (int)newId;
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync(
        ShiftDefinitionChanges changes, DateOnly deletedLastEffectiveDay, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        // 班次代号 → 保存后的 Id(新增班次的 Id 插入后才知道),用于写班组分配
        var idByCode = new Dictionary<string, int>(StringComparer.Ordinal);

        await using var conn = _factory.OpenWrite();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)
            await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var id in changes.DeletedIds)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    SoftDeleteSql,
                    new { id, effectiveTo = deletedLastEffectiveDay.ToString("yyyy-MM-dd") },
                    transaction: tx,
                    cancellationToken: cancellationToken));
            }

            foreach (var definition in changes.Added)
            {
                var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    InsertSql, ToInsertParameters(definition), transaction: tx, cancellationToken: cancellationToken));
                idByCode[definition.Code] = (int)newId;
            }

            foreach (var definition in changes.Updated)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    UpdateSql,
                    new
                    {
                        definition.Id,
                        definition.Name,
                        StartTime = definition.StartTime.ToString("HH:mm"),
                        EndTime = definition.EndTime.ToString("HH:mm"),
                        CrossesMidnight = definition.CrossesMidnight ? 1 : 0,
                        definition.TargetCount,
                        definition.ColorHex,
                        definition.DisplayOrder,
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken));
                idByCode[definition.Code] = definition.Id;
            }

            foreach (var (code, teamId) in changes.TeamByCode ?? new Dictionary<string, int>())
            {
                if (!idByCode.TryGetValue(code, out var shiftDefId))
                {
                    throw new InvalidOperationException($"班组分配引用的班次 [{code}] 不在本次保存的新增或修改列表里");
                }
                await conn.ExecuteAsync(new CommandDefinition(
                    AssignSql,
                    new
                    {
                        ShiftDefId = shiftDefId,
                        TeamId = teamId,
                        AssignedAt = nowUtc.ToString("O"),
                        AssignedByOperator = changes.ChangedBy,
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken));
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static object ToInsertParameters(ShiftDefinition definition) => new
    {
        definition.Code,
        definition.Name,
        StartTime = definition.StartTime.ToString("HH:mm"),
        EndTime = definition.EndTime.ToString("HH:mm"),
        CrossesMidnight = definition.CrossesMidnight ? 1 : 0,
        definition.TargetCount,
        definition.ColorHex,
        definition.DisplayOrder,
        EffectiveFrom = definition.EffectiveFrom.ToString("yyyy-MM-dd"),
        EffectiveTo = definition.EffectiveTo?.ToString("yyyy-MM-dd"),
        CreatedAt = definition.CreatedAt.ToString("O"),
    };

    // SQLite 把所有 INTEGER 都视作 64 位,这里映射 DTO 后再 cast 回 int。
    private sealed record ShiftRow(
        long Id, string Code, string Name, string StartTime, string EndTime,
        long CrossesMidnight, long TargetCount, string ColorHex, long DisplayOrder,
        string EffectiveFrom, string? EffectiveTo, string CreatedAt);

    private static ShiftDefinition MapShift(ShiftRow r) => new(
        (int)r.Id, r.Code, r.Name,
        TimeOnly.ParseExact(r.StartTime, "HH:mm"),
        TimeOnly.ParseExact(r.EndTime, "HH:mm"),
        r.CrossesMidnight != 0, (int)r.TargetCount, r.ColorHex, (int)r.DisplayOrder,
        DateOnly.Parse(r.EffectiveFrom),
        string.IsNullOrEmpty(r.EffectiveTo) ? null : DateOnly.Parse(r.EffectiveTo),
        DateTime.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));
}
