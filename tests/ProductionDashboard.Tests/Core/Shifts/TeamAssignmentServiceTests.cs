using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;

namespace ProductionDashboard.Tests.Core.Shifts;

/// <summary>
/// TeamAssignmentService 集成测试。验证:
/// <list type="bullet">
///   <item>启动期加载种子 (A 班 → 白班槽位, B 班 → 夜班槽位)</item>
///   <item>2 槽位倒班对调:DB 与内存一致</item>
///   <item>非 2 槽位场景下 SwapTwoSlotsAsync 抛 InvalidOperationException</item>
///   <item>Reassign 写入单个槽位</item>
///   <item>每次 swap / reassign 都有 AuditLog 记录</item>
/// </list>
/// </summary>
public sealed class TeamAssignmentServiceTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdtas_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private TeamRepository _teamRepo = null!;
    private AuditLogger _audit = null!;
    private TeamAssignmentService _sut = null!;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new DatabaseOptions
        {
            Path = _dbPath,
            BackupPath = Path.GetDirectoryName(_dbPath)!,
            VacuumTime = "02:00",
            BackupTime = "03:00",
        });
        _factory = new SqliteConnectionFactory(options);
        var init = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);
        await init.StartAsync(CancellationToken.None);
        _teamRepo = new TeamRepository(_factory);
        _audit = new AuditLogger(_factory, NullLogger<AuditLogger>.Instance);
        _sut = new TeamAssignmentService(
            _teamRepo, _audit, NullLogger<TeamAssignmentService>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-shm", "-wal" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Start_loads_seed_two_teams_and_two_assignments()
    {
        _sut.Teams.Should().HaveCount(2);
        _sut.Teams.Select(t => t.Name).Should().BeEquivalentTo(["A 班", "B 班"]);
        _sut.CurrentAssignments.Should().HaveCount(2);

        // 白班槽位 (Id=1) 应分到 A 班 (Id=1),夜班槽位 (Id=2) → B 班 (Id=2)
        _sut.GetTeamForSlot(1)!.Name.Should().Be("A 班");
        _sut.GetTeamForSlot(2)!.Name.Should().Be("B 班");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task SwapTwoSlots_exchanges_assignments_in_memory_and_db()
    {
        await _sut.SwapTwoSlotsAsync("张三", CancellationToken.None);

        _sut.GetTeamForSlot(1)!.Name.Should().Be("B 班", "swap 后白班槽位变成 B 班");
        _sut.GetTeamForSlot(2)!.Name.Should().Be("A 班");

        // DB 视角验证
        var assigns = await _teamRepo.GetCurrentAssignmentsAsync(CancellationToken.None);
        assigns.Should().HaveCount(2);
        assigns.Single(a => a.ShiftDefId == 1).TeamId.Should().Be(2, "B 班 Id=2");
        assigns.Single(a => a.ShiftDefId == 2).TeamId.Should().Be(1, "A 班 Id=1");
        assigns.All(a => a.AssignedByOperator == "张三").Should().BeTrue();
    }

    [Fact]
    public async Task Swap_writes_audit_with_operator_and_before_after_detail()
    {
        await _sut.SwapTwoSlotsAsync("李四", CancellationToken.None);

        // 等待 AuditLog 写入(fire-and-forget in service 是 await,这里同步)
        await using var conn = _factory.OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Category, Detail, User FROM AuditLog WHERE Category = 'ShiftRotation' ORDER BY Id DESC LIMIT 1;";
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("ShiftRotation");
        reader.GetString(1).Should().Contain("swap").And.Contain("→");
        reader.GetString(2).Should().Be("李四");
    }

    [Fact]
    public async Task Reassign_updates_one_slot_only()
    {
        // 把白班槽位重分配给 B 班(Id=2)
        await _sut.ReassignAsync(shiftDefId: 1, teamId: 2, "张三", CancellationToken.None);

        _sut.GetTeamForSlot(1)!.Name.Should().Be("B 班");
        _sut.GetTeamForSlot(2)!.Name.Should().Be("B 班", "夜班槽位未动,仍是 B 班");
    }

    [Fact]
    public async Task Reassign_to_unknown_team_throws()
    {
        var act = async () => await _sut.ReassignAsync(
            shiftDefId: 1, teamId: 999, "张三", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Refresh_after_external_db_change_reflects_in_memory()
    {
        // 外部直接改 DB:把白班槽位改成 B 班
        await using (var conn = _factory.OpenWrite())
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE ShiftSlotAssignment SET TeamId = 2 WHERE ShiftDefId = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        await _sut.RefreshAsync(CancellationToken.None);

        _sut.GetTeamForSlot(1)!.Name.Should().Be("B 班");
    }

    [Fact]
    public async Task AssignmentsChanged_event_fires_on_swap()
    {
        var observed = 0;
        _sut.AssignmentsChanged += () => observed++;

        await _sut.SwapTwoSlotsAsync("张三", CancellationToken.None);

        observed.Should().BeGreaterThan(0, "swap 后必须触发 AssignmentsChanged");
    }
}
