using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Plc;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;

namespace ProductionDashboard.Tests.Core.Production;

/// <summary>
/// CountingService 与真实 SQLite 的集成测试。PLC / ShiftManager / TeamAssignmentService
/// 三个依赖都用 stub 便于精确控制信号时机、班次状态与班组归属。
/// </summary>
public sealed class CountingServiceTests : IAsyncLifetime
{
    private const int TeamIdA = 1;  // 初始 schema 种子:A 班 Id=1
    private const int TeamIdB = 2;  // 初始 schema 种子:B 班 Id=2

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdct_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private ProductionEventRepository _events = null!;
    private ProductionAnalyticsRepository _analytics = null!;
    private StubPlcService _plc = null!;
    private StubShiftManager _shifts = null!;
    private StubTeamAssignmentService _teams = null!;
    private CountingService _sut = null!;

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
        _events = new ProductionEventRepository(_factory);
        _analytics = new ProductionAnalyticsRepository(_factory);

        _plc = new StubPlcService();
        _shifts = new StubShiftManager();
        _teams = new StubTeamAssignmentService();
        _sut = new CountingService(
            _plc, _shifts, _teams, _events, _analytics,
            new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance),
            NullLogger<CountingService>.Instance);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _sut.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-shm", "-wal" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static Team MakeTeam(int id, string name) => new(
        id, name, $"班长{id}", null, "#FF8C42", id, true, DateTime.UtcNow);

    private static ShiftAssignment AAssignment(DateOnly date) => new(
        new ShiftDefinition(
            1, "A", "白班", new TimeOnly(8, 0), new TimeOnly(20, 0), false, 1500, "#ff8c42", 1,
            new DateOnly(1970, 1, 1), null, DateTime.UtcNow),
        date,
        date.ToDateTime(new TimeOnly(8, 0)),
        date.ToDateTime(new TimeOnly(20, 0)));

    private static ShiftAssignment BAssignment(DateOnly date) => new(
        new ShiftDefinition(
            2, "B", "夜班", new TimeOnly(20, 0), new TimeOnly(8, 0), true, 1200, "#4a9eff", 2,
            new DateOnly(1970, 1, 1), null, DateTime.UtcNow),
        date,
        date.ToDateTime(new TimeOnly(20, 0)),
        date.AddDays(1).ToDateTime(new TimeOnly(8, 0)));

    private async Task FlushDrainAsync()
    {
        // 停止 service → channel.Complete → 排空 → 所有事件入库
        await _sut.StopAsync(CancellationToken.None);
    }

    // ── tests ────────────────────────────────────────────────────────

    /// <summary>当没有活动班次时 (例如班次间隙),PLC 信号应被丢弃,不进 DB 不计数。</summary>
    [Fact]
    public async Task Signal_with_no_active_shift_is_dropped()
    {
        _shifts.Current = null;
        await _sut.StartAsync(CancellationToken.None);

        _plc.RaiseSignal();
        _plc.RaiseSignal();
        await FlushDrainAsync();

        _sut.GetCountForTeam(TeamIdA).Should().Be(0);
    }

    /// <summary>
    /// 当前槽位无班组分配时,信号也应丢弃 — 否则归属错乱。CountingService 会写 warning。
    /// </summary>
    [Fact]
    public async Task Signal_with_no_team_for_slot_is_dropped()
    {
        var date = new DateOnly(2026, 5, 19);
        _shifts.Current = AAssignment(date);
        // _teams 默认没分配,GetTeamForSlot 返回 null
        await _sut.StartAsync(CancellationToken.None);

        _plc.RaiseSignal();
        _plc.RaiseSignal();
        await FlushDrainAsync();

        _sut.GetCountForTeam(TeamIdA).Should().Be(0);
        (await _analytics.CountEventsAsync(date, 1, CancellationToken.None)).Should().Be(0);
    }

    /// <summary>单个信号应让对应班组的内存计数器 +1,并把 ProductionEvent 带 TeamId 落到 DB。</summary>
    [Fact]
    public async Task Single_signal_increments_team_counter_and_persists_event()
    {
        var date = new DateOnly(2026, 5, 19);
        _shifts.Current = AAssignment(date);
        _teams.Assign(slot: 1, MakeTeam(TeamIdA, "A 班"));
        await _sut.StartAsync(CancellationToken.None);

        _plc.RaiseSignal();
        await FlushDrainAsync();

        _sut.GetCountForTeam(TeamIdA).Should().Be(1);

        var stored = await _analytics.CountEventsByTeamRangeAsync(
            date, date, TeamIdA, CancellationToken.None);
        stored.Should().Be(1);
    }

    /// <summary>批量信号(100 条)应全部入库 + 内存计数与 DB 计数一致(验证 channel 不丢)。</summary>
    [Fact]
    public async Task Many_signals_all_counted_and_persisted()
    {
        var date = new DateOnly(2026, 5, 19);
        _shifts.Current = AAssignment(date);
        _teams.Assign(slot: 1, MakeTeam(TeamIdA, "A 班"));
        await _sut.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100; i++) _plc.RaiseSignal();
        await FlushDrainAsync();

        _sut.GetCountForTeam(TeamIdA).Should().Be(100);
        var stored = await _analytics.CountEventsByTeamRangeAsync(
            date, date, TeamIdA, CancellationToken.None);
        stored.Should().Be(100);
    }

    /// <summary>
    /// 班次周期切换:内存计数清零;切换前的事件依然保留在旧班次的 (ShiftDefId, TeamId) 下。
    /// </summary>
    [Fact]
    public async Task ShiftChange_resets_counter_and_routes_new_signals_to_new_shift()
    {
        var d19 = new DateOnly(2026, 5, 19);
        _shifts.Current = AAssignment(d19);
        _teams.Assign(slot: 1, MakeTeam(TeamIdA, "A 班"));
        _teams.Assign(slot: 2, MakeTeam(TeamIdB, "B 班"));
        await _sut.StartAsync(CancellationToken.None);

        // 在 A 班发 5 个信号(归A 班)
        for (var i = 0; i < 5; i++) _plc.RaiseSignal();

        // 切到 B 班
        var oldA = _shifts.Current;
        var newB = BAssignment(d19);
        _shifts.TriggerShiftChange(oldA, newB);
        _sut.GetCountForTeam(TeamIdA).Should().Be(0, "切换后内存计数器清零");
        _sut.GetCountForTeam(TeamIdB).Should().Be(0);

        // 在 B 班发 3 个信号(归B 班)
        for (var i = 0; i < 3; i++) _plc.RaiseSignal();

        await FlushDrainAsync();

        _sut.GetCountForTeam(TeamIdB).Should().Be(3);

        // DB 落库:5 件归 甲(ShiftDefId=1, TeamId=1), 3 件归 乙(ShiftDefId=2, TeamId=2)
        var aJia = await _analytics.CountEventsByTeamRangeAsync(d19, d19, TeamIdA, CancellationToken.None);
        var bYi = await _analytics.CountEventsByTeamRangeAsync(d19, d19, TeamIdB, CancellationToken.None);
        aJia.Should().Be(5);
        bYi.Should().Be(3);
    }

    /// <summary>
    /// 倒班(slot↔team 重分配)后,旧班组的累计应保留,新班组从 0 开始,
    /// 但是是在内存层面 — 由 TeamAssignmentService.Swap 引起的;CountingService 自身的
    /// "班次周期切换"清零和这个独立。本测试用 DB 视角验证数据归属切换正确。
    /// </summary>
    [Fact]
    public async Task Reassign_team_redirects_subsequent_signals_to_new_team()
    {
        var date = new DateOnly(2026, 5, 19);
        _shifts.Current = AAssignment(date);
        _teams.Assign(slot: 1, MakeTeam(TeamIdA, "A 班"));
        await _sut.StartAsync(CancellationToken.None);

        // A 班先生产 4 件
        for (var i = 0; i < 4; i++) _plc.RaiseSignal();

        // 倒班:slot 1 改为B 班(模拟 TeamAssignmentService.SwapTwoSlotsAsync 后)
        _teams.Assign(slot: 1, MakeTeam(TeamIdB, "B 班"));

        // B 班在 slot 1 接着生产 6 件
        for (var i = 0; i < 6; i++) _plc.RaiseSignal();

        await FlushDrainAsync();

        // DB 视角:甲 = 4, 乙 = 6
        var jia = await _analytics.CountEventsByTeamRangeAsync(date, date, TeamIdA, CancellationToken.None);
        var yi = await _analytics.CountEventsByTeamRangeAsync(date, date, TeamIdB, CancellationToken.None);
        jia.Should().Be(4, "A 班在倒班前累计 4 件应保留");
        yi.Should().Be(6, "B 班倒班后新累计 6 件");
    }

    /// <summary>
    /// 启动期复盘:工控机中途重启后,CountingService 应按班组从 DB 把当前班次周期内
    /// 已有件数恢复到内存计数器。
    /// </summary>
    [Fact]
    public async Task Restart_mid_shift_restores_in_memory_count_by_team_from_db()
    {
        var d19 = new DateOnly(2026, 5, 19);

        // 模拟之前已经累计了 42 件(A 班在 A 槽位)
        // 关键:Timestamp 必须落在 A 班次的本地时段 [08:00, 20:00) 转 UTC 内,否则
        // ReloadCurrentPeriodAsync 用时间窗筛选不到。直接用当地 12:00 转 UTC。
        var noonLocal = d19.ToDateTime(new TimeOnly(12, 0));
        var noonUtc = noonLocal.ToUniversalTime();
        var existing = Enumerable.Range(0, 42)
            .Select(i => new ProductionEvent(0, noonUtc.AddSeconds(i), d19, 1, TeamIdA))
            .ToList();
        await _events.InsertEventsAsync(existing, CancellationToken.None);

        // 应用重启,CountingService 重新启动 — 当前班次为 A,槽位 1 归甲
        _shifts.Current = AAssignment(d19);
        _teams.Assign(slot: 1, MakeTeam(TeamIdA, "A 班"));
        await _sut.StartAsync(CancellationToken.None);

        _sut.GetCountForTeam(TeamIdA).Should().Be(42, "从 DB 恢复历史计数");

        _plc.RaiseSignal();
        await FlushDrainAsync();

        _sut.GetCountForTeam(TeamIdA).Should().Be(43, "新信号在恢复值之上继续累计");
    }

    /// <summary>每个信号都应触发 TeamCountChanged 事件,参数为 (TeamId, 新累计)。</summary>
    [Fact]
    public async Task TeamCountChanged_event_fires_per_signal_with_correct_team()
    {
        var date = new DateOnly(2026, 5, 19);
        _shifts.Current = AAssignment(date);
        _teams.Assign(slot: 1, MakeTeam(TeamIdA, "A 班"));
        await _sut.StartAsync(CancellationToken.None);

        var observed = new List<(int teamId, int count)>();
        _sut.TeamCountChanged += (t, c) => observed.Add((t, c));

        _plc.RaiseSignal();
        _plc.RaiseSignal();
        _plc.RaiseSignal();
        await FlushDrainAsync();

        observed.Should().Equal(
            (TeamIdA, 1), (TeamIdA, 2), (TeamIdA, 3));
    }

    // ── stubs ────────────────────────────────────────────────────────

    private sealed class StubPlcService : IPlcService
    {
        public bool IsConnected { get; set; } = true;
        public event Action<DateTime>? CompleteSignalReceived;
        public event Action<bool>? ConnectionStateChanged;

        public void RaiseSignal() =>
            CompleteSignalReceived?.Invoke(DateTime.UtcNow);

        public void RaiseConnection(bool isConnected)
        {
            IsConnected = isConnected;
            ConnectionStateChanged?.Invoke(isConnected);
        }
    }

    private sealed class StubShiftManager : IShiftManager
    {
        public ShiftAssignment? Current { get; set; }
        public IReadOnlyList<ShiftDefinition> Definitions { get; set; } = Array.Empty<ShiftDefinition>();
        public event Action<ShiftAssignment?, ShiftAssignment?>? ShiftChanged;
#pragma warning disable CS0067
        public event Action? DefinitionsChanged;
#pragma warning restore CS0067
        public ShiftAssignment? GetShiftAt(DateTime localTime) => Current;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;

        public void TriggerShiftChange(ShiftAssignment? oldS, ShiftAssignment? newS)
        {
            Current = newS;
            ShiftChanged?.Invoke(oldS, newS);
        }
    }

    private sealed class StubTeamAssignmentService : ITeamAssignmentService
    {
        private readonly Dictionary<int, Team> _slots = new();
        private readonly List<Team> _teams = new();

        public IReadOnlyList<Team> Teams => _teams;
        public IReadOnlyDictionary<int, Team> CurrentAssignments => _slots;

        public Team? GetTeamForSlot(int shiftDefId)
            => _slots.TryGetValue(shiftDefId, out var t) ? t : null;

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SwapTwoSlotsAsync(string operatorName, CancellationToken cancellationToken)
            => throw new NotSupportedException("此 stub 不实现 swap");

        public Task ReassignAsync(int shiftDefId, int teamId, string operatorName, CancellationToken cancellationToken)
            => throw new NotSupportedException("此 stub 不实现 reassign");

#pragma warning disable CS0067
        public event Action? AssignmentsChanged;
#pragma warning restore CS0067

        public void Assign(int slot, Team team)
        {
            _slots[slot] = team;
            if (!_teams.Contains(team)) _teams.Add(team);
        }
    }
}
