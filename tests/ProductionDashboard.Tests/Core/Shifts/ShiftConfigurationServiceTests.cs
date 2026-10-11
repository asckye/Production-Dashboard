using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Tests.Core.Shifts;

/// <summary><see cref="ShiftConfigurationService"/>:保存班次 / 班组修改,并让运行中的服务重新加载。</summary>
public class ShiftConfigurationServiceTests
{
    private readonly RecordingShiftRepository _shiftRepo = new();
    private readonly RecordingTeamRepository _teamRepo = new();
    private readonly CountingShiftManager _shifts = new();
    private readonly CountingTeamAssignment _teams = new();

    private ShiftConfigurationService CreateService() => new(
        _shiftRepo, _teamRepo, _shifts, _teams,
        new FixedClock(new DateTimeOffset(2026, 10, 11, 1, 30, 0, TimeSpan.Zero)), // 本地 (UTC+8) 2026-10-11 09:30
        NullLogger<ShiftConfigurationService>.Instance);

    [Fact]
    public async Task Saving_shift_changes_writes_them_in_one_call_then_reloads_shifts()
    {
        var changes = new ShiftDefinitionChanges(DeletedIds: [1], Added: [Shift(0, "C")], Updated: [Shift(2, "B")]);

        await CreateService().SaveShiftDefinitionsAsync(changes, CancellationToken.None);

        _shiftRepo.Saved.Should().ContainSingle().Which.Should().Be(changes);
        _shifts.RefreshCount.Should().Be(1, "保存后运行中的 ShiftManager 必须重新加载班次");
    }

    [Fact]
    public async Task Deleted_shift_stops_taking_effect_today()
    {
        await CreateService().SaveShiftDefinitionsAsync(
            new ShiftDefinitionChanges(DeletedIds: [1], Added: [], Updated: []),
            CancellationToken.None);

        // 本地日期 2026-10-11 → 最后生效日是 10-10,当天重新加载时就不会再出现
        _shiftRepo.DeletedLastEffectiveDay.Should().Be(new DateOnly(2026, 10, 10));
    }

    [Fact]
    public async Task Saving_teams_updates_each_team_then_reloads_assignments()
    {
        var teamA = new Team(1, "甲班", "王班长", null, "#FF8C42", 1, true, DateTime.UnixEpoch);
        var teamB = new Team(2, "乙班", "赵班长", null, "#4A9EFF", 2, true, DateTime.UnixEpoch);

        await CreateService().SaveTeamsAsync([teamA, teamB], CancellationToken.None);

        _teamRepo.Updated.Should().Equal(teamA, teamB);
        _teams.RefreshCount.Should().Be(1);
    }

    private static ShiftDefinition Shift(int id, string code) => new(
        id, code, code, new TimeOnly(8, 0), new TimeOnly(16, 0), false, 1000, "#FFFFFF", 1,
        new DateOnly(2026, 1, 1), null, DateTime.UnixEpoch);

    // ── 测试桩 ─────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }

    private sealed class RecordingShiftRepository : IShiftDefinitionRepository
    {
        public List<ShiftDefinitionChanges> Saved { get; } = [];
        public DateOnly? DeletedLastEffectiveDay { get; private set; }

        public Task SaveChangesAsync(
            ShiftDefinitionChanges changes, DateOnly deletedLastEffectiveDay, CancellationToken cancellationToken)
        {
            Saved.Add(changes);
            DeletedLastEffectiveDay = deletedLastEffectiveDay;
            return Task.CompletedTask;
        }

        public Task<int> InsertShiftDefinitionAsync(ShiftDefinition definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ShiftDefinition>> GetActiveShiftDefinitionsAsync(DateOnly asOf, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ShiftDefinition?> GetShiftDefinitionAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTeamRepository : ITeamRepository
    {
        public List<Team> Updated { get; } = [];

        public Task UpdateTeamAsync(Team team, CancellationToken cancellationToken)
        {
            Updated.Add(team);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Team>> GetActiveTeamsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Team?> GetTeamAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> InsertTeamAsync(Team team, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeactivateTeamAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftSlotAssignment>> GetCurrentAssignmentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ShiftSlotAssignment?> GetAssignmentAsync(int shiftDefId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertAssignmentAsync(ShiftSlotAssignment assignment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertAssignmentsAtomicAsync(IReadOnlyList<ShiftSlotAssignment> assignments, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingShiftManager : IShiftManager
    {
        public int RefreshCount { get; private set; }

        public ShiftAssignment? Current => null;
        public IReadOnlyList<ShiftDefinition> Definitions => [];
        public event Action<ShiftAssignment?, ShiftAssignment?>? ShiftChanged { add { } remove { } }
        public event Action? DefinitionsChanged { add { } remove { } }

        public ShiftAssignment? GetShiftAt(DateTime localTime) => null;

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingTeamAssignment : ITeamAssignmentService
    {
        public int RefreshCount { get; private set; }

        public IReadOnlyList<Team> Teams => [];
        public IReadOnlyDictionary<int, Team> CurrentAssignments => new Dictionary<int, Team>();
        public event Action? AssignmentsChanged { add { } remove { } }

        public Team? GetTeamForSlot(int shiftDefId) => null;

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task SwapTwoSlotsAsync(string operatorName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReassignAsync(int shiftDefId, int teamId, string operatorName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
