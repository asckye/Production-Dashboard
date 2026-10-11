using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;
using ProductionDashboard.Tests.Helpers;

namespace ProductionDashboard.Tests.Core.Production;

/// <summary>
/// MinuteAggregator(从 AggregationHostedService 拆出)的集成测试。覆盖审查指出的零测试盲区:
/// 上一分钟事件聚合成带 TeamId 的 ProductionMinute 行、无事件不写、空档期不写。
/// </summary>
public sealed class MinuteAggregatorTests : IAsyncLifetime
{
    private const int TeamA = 1;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdma_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private ProductionEventRepository _events = null!;
    private ProductionAnalyticsRepository _analytics = null!;
    private ControlledTimeProvider _clock = null!;
    private DateTime _prevMinute;
    private DateOnly _shiftDate;

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
        _clock = new ControlledTimeProvider(); // now = 2026-01-01 00:00:00 UTC

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var thisMinute = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        _prevMinute = thisMinute.AddMinutes(-1);
        _shiftDate = DateOnly.FromDateTime(_prevMinute);
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

    private MinuteAggregator MakeSut(ShiftAssignment? shift, int? teamForSlot)
    {
        var shifts = new StubShiftManager { ShiftAt = shift };
        var teams = new StubTeams { TeamForSlot = teamForSlot };
        return new MinuteAggregator(
            _events, _analytics, shifts, teams,
            new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance),
            NullLogger<MinuteAggregator>.Instance, _clock);
    }

    private ShiftAssignment AShift() => new(
        new ShiftDefinition(
            1, "A", "白班", new TimeOnly(8, 0), new TimeOnly(20, 0),
            false, 1500, "#ff8c42", 1, new DateOnly(1970, 1, 1), null, DateTime.UtcNow),
        _shiftDate,
        _shiftDate.ToDateTime(new TimeOnly(8, 0)),
        _shiftDate.ToDateTime(new TimeOnly(20, 0)));

    private async Task SeedPrevMinuteEventsAsync(int n)
    {
        var list = Enumerable.Range(0, n)
            .Select(i => new ProductionEvent(0, _prevMinute.AddSeconds(10 + i), _shiftDate, 1, TeamA))
            .ToList();
        await _events.InsertEventsAsync(list, CancellationToken.None);
    }

    private async Task<(long count, long teamId)?> ReadMinuteRowAsync()
    {
        await using var conn = _factory.OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Count, TeamId FROM ProductionMinute WHERE MinuteStart = @m";
        cmd.Parameters.AddWithValue("@m", _prevMinute.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact]
    public async Task AggregateAsync_writes_minute_bucket_with_team_for_prev_minute()
    {
        await SeedPrevMinuteEventsAsync(3);
        var sut = MakeSut(AShift(), teamForSlot: TeamA);

        await sut.AggregateAsync(CancellationToken.None);

        var row = await ReadMinuteRowAsync();
        row.Should().NotBeNull();
        row!.Value.count.Should().Be(3);
        row.Value.teamId.Should().Be(TeamA);
    }

    [Fact]
    public async Task AggregateAsync_writes_nothing_when_no_events()
    {
        var sut = MakeSut(AShift(), teamForSlot: TeamA);

        await sut.AggregateAsync(CancellationToken.None);

        (await ReadMinuteRowAsync()).Should().BeNull("无事件时不应写分钟桶");
    }

    [Fact]
    public async Task AggregateAsync_skips_when_in_gap_no_shift()
    {
        await SeedPrevMinuteEventsAsync(2);
        var sut = MakeSut(shift: null, teamForSlot: null); // 空档期

        await sut.AggregateAsync(CancellationToken.None);

        (await ReadMinuteRowAsync()).Should().BeNull("空档期的事件不归任何班次,不聚合");
    }

    // ── stubs ──────────────────────────────────────────────────────────

    private sealed class StubShiftManager : IShiftManager
    {
        public ShiftAssignment? ShiftAt { get; set; }
        public ShiftAssignment? Current { get; set; }
        public IReadOnlyList<ShiftDefinition> Definitions { get; set; } = Array.Empty<ShiftDefinition>();
#pragma warning disable CS0067
        public event Action<ShiftAssignment?, ShiftAssignment?>? ShiftChanged;
        public event Action? DefinitionsChanged;
#pragma warning restore CS0067
        public ShiftAssignment? GetShiftAt(DateTime localTime) => ShiftAt;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubTeams : ITeamAssignmentService
    {
        public int? TeamForSlot { get; set; }
        public IReadOnlyList<Team> Teams => Array.Empty<Team>();
        public IReadOnlyDictionary<int, Team> CurrentAssignments => new Dictionary<int, Team>();
        public Team? GetTeamForSlot(int shiftDefId)
            => TeamForSlot is null ? null
               : new Team(TeamForSlot.Value, $"班{TeamForSlot}", "班长", null, "#fff", TeamForSlot.Value, true, DateTime.UtcNow);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SwapTwoSlotsAsync(string operatorName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReassignAsync(int shiftDefId, int teamId, string operatorName, CancellationToken cancellationToken) => Task.CompletedTask;
#pragma warning disable CS0067
        public event Action? AssignmentsChanged;
#pragma warning restore CS0067
    }
}
