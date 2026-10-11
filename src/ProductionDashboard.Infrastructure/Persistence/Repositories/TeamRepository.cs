using Dapper;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="ITeamRepository"/>
public sealed class TeamRepository : ITeamRepository
{
    private readonly IDbConnectionFactory _factory;

    /// <summary>注入连接工厂(读 / 写连接由 factory 决定 readonly/journal 模式)。</summary>
    public TeamRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    // ── Team CRUD ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Team>> GetActiveTeamsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name, LeaderName, Members, ColorHex, DisplayOrder, IsActive, CreatedAt
            FROM Team
            WHERE IsActive = 1
            ORDER BY DisplayOrder;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<TeamRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(MapTeam).ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name, LeaderName, Members, ColorHex, DisplayOrder, IsActive, CreatedAt
            FROM Team
            ORDER BY IsActive DESC, DisplayOrder;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<TeamRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(MapTeam).ToArray();
    }

    /// <inheritdoc/>
    public async Task<Team?> GetTeamAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name, LeaderName, Members, ColorHex, DisplayOrder, IsActive, CreatedAt
            FROM Team
            WHERE Id = @id;
            """;

        await using var conn = _factory.OpenRead();
        var row = await conn.QuerySingleOrDefaultAsync<TeamRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
        return row is null ? null : MapTeam(row);
    }

    /// <inheritdoc/>
    public async Task<int> InsertTeamAsync(Team team, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(team);

        const string sql = """
            INSERT INTO Team
                (Name, LeaderName, Members, ColorHex, DisplayOrder, IsActive, CreatedAt)
            VALUES
                (@Name, @LeaderName, @Members, @ColorHex, @DisplayOrder, @IsActive, @CreatedAt);
            SELECT last_insert_rowid();
            """;

        await using var conn = _factory.OpenWrite();
        var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
            {
                team.Name,
                team.LeaderName,
                team.Members,
                team.ColorHex,
                team.DisplayOrder,
                IsActive = team.IsActive ? 1 : 0,
                CreatedAt = team.CreatedAt.ToString("O"),
            },
            cancellationToken: cancellationToken));
        return (int)newId;
    }

    /// <inheritdoc/>
    public async Task UpdateTeamAsync(Team team, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(team);

        const string sql = """
            UPDATE Team SET
                Name         = @Name,
                LeaderName   = @LeaderName,
                Members      = @Members,
                ColorHex     = @ColorHex,
                DisplayOrder = @DisplayOrder,
                IsActive     = @IsActive
            WHERE Id = @Id;
            """;

        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                team.Id,
                team.Name,
                team.LeaderName,
                team.Members,
                team.ColorHex,
                team.DisplayOrder,
                IsActive = team.IsActive ? 1 : 0,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task DeactivateTeamAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE Team SET IsActive = 0 WHERE Id = @id;";
        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
    }

    // ── ShiftSlotAssignment ──────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShiftSlotAssignment>> GetCurrentAssignmentsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ShiftDefId, TeamId, AssignedAt, AssignedByOperator
            FROM ShiftSlotAssignment
            ORDER BY ShiftDefId;
            """;

        await using var conn = _factory.OpenRead();
        var rows = await conn.QueryAsync<AssignmentRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(MapAssignment).ToArray();
    }

    /// <inheritdoc/>
    public async Task<ShiftSlotAssignment?> GetAssignmentAsync(
        int shiftDefId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ShiftDefId, TeamId, AssignedAt, AssignedByOperator
            FROM ShiftSlotAssignment
            WHERE ShiftDefId = @shiftDefId;
            """;

        await using var conn = _factory.OpenRead();
        var row = await conn.QuerySingleOrDefaultAsync<AssignmentRow>(new CommandDefinition(
            sql, new { shiftDefId }, cancellationToken: cancellationToken));
        return row is null ? null : MapAssignment(row);
    }

    /// <inheritdoc/>
    public async Task UpsertAssignmentAsync(
        ShiftSlotAssignment assignment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        const string sql = """
            INSERT INTO ShiftSlotAssignment
                (ShiftDefId, TeamId, AssignedAt, AssignedByOperator)
            VALUES
                (@ShiftDefId, @TeamId, @AssignedAt, @AssignedByOperator)
            ON CONFLICT(ShiftDefId) DO UPDATE SET
                TeamId             = excluded.TeamId,
                AssignedAt         = excluded.AssignedAt,
                AssignedByOperator = excluded.AssignedByOperator;
            """;

        await using var conn = _factory.OpenWrite();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                assignment.ShiftDefId,
                assignment.TeamId,
                AssignedAt = assignment.AssignedAt.ToString("O"),
                assignment.AssignedByOperator,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task UpsertAssignmentsAtomicAsync(
        IReadOnlyList<ShiftSlotAssignment> assignments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0) return;

        const string sql = """
            INSERT INTO ShiftSlotAssignment
                (ShiftDefId, TeamId, AssignedAt, AssignedByOperator)
            VALUES
                (@ShiftDefId, @TeamId, @AssignedAt, @AssignedByOperator)
            ON CONFLICT(ShiftDefId) DO UPDATE SET
                TeamId             = excluded.TeamId,
                AssignedAt         = excluded.AssignedAt,
                AssignedByOperator = excluded.AssignedByOperator;
            """;

        await using var conn = _factory.OpenWrite();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)
            await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var a in assignments)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    sql,
                    new
                    {
                        a.ShiftDefId,
                        a.TeamId,
                        AssignedAt = a.AssignedAt.ToString("O"),
                        a.AssignedByOperator,
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken));
            }
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ── 行映射 ────────────────────────────────────────────────────────

    private sealed record TeamRow(
        long Id, string Name, string LeaderName, string? Members,
        string ColorHex, long DisplayOrder, long IsActive, string CreatedAt);

    private static Team MapTeam(TeamRow r) => new(
        (int)r.Id, r.Name, r.LeaderName, r.Members,
        r.ColorHex, (int)r.DisplayOrder, r.IsActive != 0,
        DateTime.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));

    private sealed record AssignmentRow(
        long ShiftDefId, long TeamId, string AssignedAt, string? AssignedByOperator);

    private static ShiftSlotAssignment MapAssignment(AssignmentRow r) => new(
        (int)r.ShiftDefId, (int)r.TeamId,
        DateTime.Parse(r.AssignedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.AssignedByOperator);
}
