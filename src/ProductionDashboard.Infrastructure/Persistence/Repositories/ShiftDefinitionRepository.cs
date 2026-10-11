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

    /// <inheritdoc/>
    public async Task UpdateShiftDefinitionAsync(
        ShiftDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        const string sql = """
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

        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
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
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<int> InsertShiftDefinitionAsync(
        ShiftDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        const string sql = """
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

        await using var conn = _factory.OpenWrite();
        var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
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
            },
            cancellationToken: cancellationToken));
        return (int)newId;
    }

    /// <inheritdoc/>
    public async Task SoftDeleteShiftDefinitionAsync(
        int id, DateOnly effectiveTo, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE ShiftDefinition SET EffectiveTo = @effectiveTo WHERE Id = @id;
            """;

        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { id, effectiveTo = effectiveTo.ToString("yyyy-MM-dd") },
            cancellationToken: cancellationToken));
    }

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
