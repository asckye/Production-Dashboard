using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;

namespace ProductionDashboard.Tests.Infrastructure.Persistence;

/// <summary>
/// 5 个 Repository(ShiftDefinition / ProductionEvent / ShiftSummary /
/// Analytics / DataLifecycle)的集成测试。共享同一个临时 SQLite 文件,
/// 测试 SQL 与表映射;每个测试独立 db 文件,跑完清理。
/// </summary>
public sealed class RepositoriesIntegrationTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdtest_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private ShiftDefinitionRepository _shiftDefs = null!;
    private ProductionEventRepository _events = null!;
    private ShiftSummaryRepository _summaries = null!;
    private ProductionAnalyticsRepository _analytics = null!;

    /// <summary>每个测试前:创建独立 SQLite 文件,跑 v1 迁移(含种子数据),实例化 5 个 Repository。</summary>
    public async Task InitializeAsync()
    {
        var options = Options.Create(new DatabaseOptions
        {
            Path = _dbPath,
            BackupPath = Path.GetDirectoryName(_dbPath)!,
            BackupRetentionDays = 7,
            VacuumTime = "02:00",
            BackupTime = "03:00",
        });

        _factory = new SqliteConnectionFactory(options);
        var initializer = new DatabaseInitializer(
            _factory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.StartAsync(CancellationToken.None);

        _shiftDefs = new ShiftDefinitionRepository(_factory);
        _events = new ProductionEventRepository(_factory);
        _summaries = new ShiftSummaryRepository(_factory);
        _analytics = new ProductionAnalyticsRepository(_factory);
    }

    /// <summary>每个测试后:释放连接池 + 删 db 文件(含 -shm / -wal 边带文件)。</summary>
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
        catch
        {
            // 测试清理失败不影响断言
        }
        return Task.CompletedTask;
    }

    /// <summary>迁移 v1 应注入默认 A 班 / B 班两个种子,字段全部正确。</summary>
    [Fact]
    public async Task Seed_inserts_default_A_and_B_shifts()
    {
        var shifts = await _shiftDefs.GetActiveShiftDefinitionsAsync(
            DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        shifts.Should().HaveCount(2);

        var a = shifts.Single(s => s.Code == "A");
        a.Name.Should().Be("白班");
        a.StartTime.Should().Be(new TimeOnly(8, 0));
        a.EndTime.Should().Be(new TimeOnly(20, 0));
        a.CrossesMidnight.Should().BeFalse();
        a.TargetCount.Should().Be(1500);
        a.ColorHex.Should().Be("#ff8c42");

        var b = shifts.Single(s => s.Code == "B");
        b.Name.Should().Be("夜班");
        b.CrossesMidnight.Should().BeTrue();
        b.TargetCount.Should().Be(1200);
        b.ColorHex.Should().Be("#4a9eff");
    }

    /// <summary>根据 Id 查询班次定义,存在时返回完整记录。</summary>
    [Fact]
    public async Task GetShiftDefinitionById_returns_existing_record()
    {
        var fetched = await _shiftDefs.GetShiftDefinitionAsync(1, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.Code.Should().Be("A");
    }

    /// <summary>Id 不存在时返回 null,不抛异常(调用方按可空处理)。</summary>
    [Fact]
    public async Task GetShiftDefinitionById_returns_null_when_missing()
    {
        var fetched = await _shiftDefs.GetShiftDefinitionAsync(9999, CancellationToken.None);
        fetched.Should().BeNull();
    }

    /// <summary>批量插入 50 条事件后,CountEvents 应返回 50。</summary>
    [Fact]
    public async Task InsertEvents_then_CountEvents_returns_correct_total()
    {
        var shiftDate = new DateOnly(2026, 5, 19);
        var events = Enumerable.Range(0, 50)
            .Select(i => new ProductionEvent(
                Id: 0,
                Timestamp: DateTime.UtcNow.AddSeconds(i),
                ShiftDate: shiftDate,
                ShiftDefId: 1,
                TeamId: 1))
            .ToList();

        var inserted = await _events.InsertEventsAsync(events, CancellationToken.None);
        inserted.Should().Be(50);

        var count = await _analytics.CountEventsAsync(shiftDate, 1, CancellationToken.None);
        count.Should().Be(50);
    }

    /// <summary>空集合插入应快速短路返回 0,不发起 DB 事务。</summary>
    [Fact]
    public async Task InsertEvents_with_empty_collection_returns_zero()
    {
        var inserted = await _events.InsertEventsAsync(
            Array.Empty<ProductionEvent>(), CancellationToken.None);
        inserted.Should().Be(0);
    }

    /// <summary>CountEvents 必须按 (ShiftDate, ShiftDefId) 精确过滤,不能跨班次或跨日期串数据。</summary>
    [Fact]
    public async Task CountEvents_excludes_other_shifts_and_dates()
    {
        var d1 = new DateOnly(2026, 5, 19);
        var d2 = new DateOnly(2026, 5, 20);
        var t = DateTime.UtcNow;

        await _events.InsertEventsAsync(
        [
            new ProductionEvent(0, t, d1, ShiftDefId: 1, TeamId: 1),
            new ProductionEvent(0, t, d1, ShiftDefId: 1, TeamId: 1),
            new ProductionEvent(0, t, d1, ShiftDefId: 2, TeamId: 2),  // 别的班次
            new ProductionEvent(0, t, d2, ShiftDefId: 1, TeamId: 1),  // 别的日期
        ], CancellationToken.None);

        var only = await _analytics.CountEventsAsync(d1, 1, CancellationToken.None);
        only.Should().Be(2);
    }

    /// <summary>
    /// UpsertShiftSummary 应当 INSERT-OR-UPDATE:同一 (ShiftDate, ShiftDefId, TeamId) 第一次 INSERT,
    /// 第二次只更新非主键字段,不抛唯一约束冲突。
    /// </summary>
    [Fact]
    public async Task UpsertShiftSummary_inserts_then_updates_same_key()
    {
        var shiftDate = new DateOnly(2026, 5, 19);

        var first = new ShiftSummary(
            shiftDate,
            ShiftDefId: 1,
            TeamId: 1,
            ShiftName: "白班",
            StartTime: new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc),
            EndTime: null,
            TargetCount: 1500,
            ActualCount: 100,
            PeakHourCount: null,
            Remark: null);

        await _summaries.UpsertShiftSummaryAsync(first, CancellationToken.None);

        var updated = first with
        {
            ActualCount = 1247,
            PeakHourCount = 210,
            EndTime = new DateTime(2026, 5, 19, 20, 0, 0, DateTimeKind.Utc),
            Remark = "正常下班",
        };
        await _summaries.UpsertShiftSummaryAsync(updated, CancellationToken.None);

        var read = await _summaries.GetShiftSummaryAsync(shiftDate, 1, teamId: 1, CancellationToken.None);
        read.Should().NotBeNull();
        read!.ActualCount.Should().Be(1247);
        read.PeakHourCount.Should().Be(210);
        read.EndTime.Should().NotBeNull();
        read.Remark.Should().Be("正常下班");
    }

    /// <summary>查询不存在的班次汇总应返回 null,不抛异常。</summary>
    [Fact]
    public async Task GetShiftSummary_returns_null_when_missing()
    {
        var read = await _summaries.GetShiftSummaryAsync(
            new DateOnly(2099, 1, 1), 1, teamId: 1, CancellationToken.None);
        read.Should().BeNull();
    }

    // ── CountEventsInRangeAsync / PeakHour / MinuteAggregate ─────

    /// <summary>CountEventsInRange 应按 Timestamp 闭开区间 [start, end) 精确过滤事件。</summary>
    [Fact]
    public async Task CountEventsInRange_counts_only_events_inside_window()
    {
        var d = new DateOnly(2026, 5, 19);
        var t0 = new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

        await _events.InsertEventsAsync(new[]
        {
            new ProductionEvent(0, t0.AddSeconds(10), d, 1, TeamId: 1),  // 10:00:10 in
            new ProductionEvent(0, t0.AddSeconds(30), d, 1, TeamId: 1),  // 10:00:30 in
            new ProductionEvent(0, t0.AddSeconds(70), d, 1, TeamId: 1),  // 10:01:10 out
            new ProductionEvent(0, t0.AddSeconds(-5), d, 1, TeamId: 1),  // 09:59:55 out
        }, CancellationToken.None);

        var inWindow = await _analytics.CountEventsInRangeAsync(
            t0, t0.AddMinutes(1), CancellationToken.None);

        inWindow.Should().Be(2);
    }

    /// <summary>峰值小时统计应返回各小时桶中事件最多的那一桶的件数(用于显示班次峰值)。</summary>
    [Fact]
    public async Task ComputePeakHourCount_returns_max_events_in_any_hour()
    {
        var d = new DateOnly(2026, 5, 19);
        // 8:00 时段 3 件, 9:00 时段 7 件, 10:00 时段 2 件
        var events = new List<ProductionEvent>();
        for (var i = 0; i < 3; i++)
            events.Add(new ProductionEvent(0, new DateTime(2026, 5, 19, 8, i, 0, DateTimeKind.Utc), d, 1, TeamId: 1));
        for (var i = 0; i < 7; i++)
            events.Add(new ProductionEvent(0, new DateTime(2026, 5, 19, 9, i, 0, DateTimeKind.Utc), d, 1, TeamId: 1));
        for (var i = 0; i < 2; i++)
            events.Add(new ProductionEvent(0, new DateTime(2026, 5, 19, 10, i, 0, DateTimeKind.Utc), d, 1, TeamId: 1));
        await _events.InsertEventsAsync(events, CancellationToken.None);

        var peak = await _analytics.ComputePeakHourCountAsync(d, 1, CancellationToken.None);

        peak.Should().Be(7);
    }

    /// <summary>无事件时峰值小时应返回 0,而非 null,方便 UI 直接显示。</summary>
    [Fact]
    public async Task ComputePeakHourCount_returns_zero_when_no_events()
    {
        var peak = await _analytics.ComputePeakHourCountAsync(
            new DateOnly(2099, 1, 1), 1, CancellationToken.None);
        peak.Should().Be(0);
    }

    private Task InsertEventsAsync(DateTime minuteUtc, DateOnly shiftDate, int shiftDefId, int teamId, int count) =>
        _events.InsertEventsAsync(
            Enumerable.Range(0, count)
                .Select(i => new ProductionEvent(0, minuteUtc.AddMilliseconds(i * 500), shiftDate, shiftDefId, teamId))
                .ToList(),
            CancellationToken.None);

    /// <summary>
    /// 分钟聚合按原始事件重算:同一分钟重复计算只保留一行,Count 始终等于该分钟的事件数(含后来补进的事件)。
    /// </summary>
    [Fact]
    public async Task RebuildMinuteAggregates_counts_events_per_minute_and_is_repeatable()
    {
        var minute = new DateTime(2026, 5, 19, 10, 5, 0, DateTimeKind.Utc);
        var d = new DateOnly(2026, 5, 19);
        var window = (from: minute, to: minute.AddMinutes(1));

        await InsertEventsAsync(minute, d, 1, 1, 30);
        (await _events.RebuildMinuteAggregatesAsync(window.from, window.to, CancellationToken.None)).Should().Be(1);

        // 聚合之后又有 12 个事件晚落库,再算一次
        await InsertEventsAsync(minute.AddSeconds(20), d, 1, 1, 12);
        await _events.RebuildMinuteAggregatesAsync(window.from, window.to, CancellationToken.None);

        // 验证只有一行,Count 是重算后的值
        await using var conn = ((SqliteConnectionFactory)_factory!).OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Count FROM ProductionMinute WHERE MinuteStart = @m";
        cmd.Parameters.AddWithValue("@m", minute.ToString("O"));
        var stored = (long)(await cmd.ExecuteScalarAsync())!;
        stored.Should().Be(42);

        var rowCountCmd = conn.CreateCommand();
        rowCountCmd.CommandText = "SELECT COUNT(*) FROM ProductionMinute WHERE MinuteStart = @m";
        rowCountCmd.Parameters.AddWithValue("@m", minute.ToString("O"));
        var rowCount = (long)(await rowCountCmd.ExecuteScalarAsync())!;
        rowCount.Should().Be(1, "ON CONFLICT 应该只保留一行");

        (await _events.GetLatestAggregatedMinuteAsync(CancellationToken.None)).Should().Be(minute);
    }

    /// <summary>还没有任何分钟聚合时,最新聚合分钟为 null。</summary>
    [Fact]
    public async Task GetLatestAggregatedMinute_is_null_when_nothing_aggregated()
    {
        (await _events.GetLatestAggregatedMinuteAsync(CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// 聚合分层:CountEventsByShiftDateRangeAsync 现在从 ProductionMinute 分钟桶 SUM(Count)。
    /// 验证范围求和、shiftDefId 过滤、空范围为 0,以及 TeamId 确实写入分钟桶(此前是废列)。
    /// </summary>
    [Fact]
    public async Task CountEventsByShiftDateRange_sums_minute_buckets_and_persists_team()
    {
        var d1 = new DateOnly(2026, 3, 1);
        var d2 = new DateOnly(2026, 3, 2);
        var t = new TimeOnly(8, 0);

        // d1 班次1/班组1:30 + 20;d2 班次2/班组2:15
        await InsertEventsAsync(d1.ToDateTime(t).ToUniversalTime(), d1, 1, 1, 30);
        await InsertEventsAsync(d1.ToDateTime(t.AddMinutes(1)).ToUniversalTime(), d1, 1, 1, 20);
        await InsertEventsAsync(d2.ToDateTime(t).ToUniversalTime(), d2, 2, 2, 15);
        await _events.RebuildMinuteAggregatesAsync(
            d1.ToDateTime(TimeOnly.MinValue).ToUniversalTime(),
            d2.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime(),
            CancellationToken.None);

        // 全范围 = 65
        (await _analytics.CountEventsByShiftDateRangeAsync(d1, d2, null, CancellationToken.None))
            .Should().Be(65);
        // 仅 d1 = 50
        (await _analytics.CountEventsByShiftDateRangeAsync(d1, d1, null, CancellationToken.None))
            .Should().Be(50);
        // 按 shiftDefId = 2 → 15
        (await _analytics.CountEventsByShiftDateRangeAsync(d1, d2, 2, CancellationToken.None))
            .Should().Be(15);
        // 无数据范围 → 0(COALESCE 兜底)
        (await _analytics.CountEventsByShiftDateRangeAsync(
            new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 2), null, CancellationToken.None))
            .Should().Be(0);

        // TeamId 确实写入了分钟桶(此前从不填,是废列 + 废索引)
        await using var conn = ((SqliteConnectionFactory)_factory!).OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(Count),0) FROM ProductionMinute WHERE TeamId = 1";
        var team1Sum = (long)(await cmd.ExecuteScalarAsync())!;
        team1Sum.Should().Be(50, "班组1的分钟桶 TeamId 应正确写入");
    }

    /// <summary>
    /// 保存班次时一并写班组分配:新增班次拿到新 Id 后立即分配;被删除班次的分配一并删除,
    /// 这样"当前分配"里只剩仍在生效的班次。
    /// </summary>
    [Fact]
    public async Task SaveChanges_assigns_teams_to_new_shifts_and_drops_assignments_of_deleted_ones()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var teams = new TeamRepository(_factory);
        var added = new ShiftDefinition(
            0, "C", "中班", new TimeOnly(16, 0), new TimeOnly(0, 0), true, 800, "#00ff88", 3,
            today, null, DateTime.UtcNow);

        await _shiftDefs.SaveChangesAsync(
            new ShiftDefinitionChanges(
                DeletedIds: [2], Added: [added], Updated: [],
                TeamByCode: new Dictionary<string, int> { ["C"] = 2 }, ChangedBy: "张班长"),
            today.AddDays(-1),
            DateTime.UtcNow,
            CancellationToken.None);

        var newId = (await _shiftDefs.GetActiveShiftDefinitionsAsync(today, CancellationToken.None)).Single(s => s.Code == "C").Id;
        var assignments = await teams.GetCurrentAssignmentsAsync(CancellationToken.None);
        assignments.Select(a => (a.ShiftDefId, a.TeamId)).Should().BeEquivalentTo(
            [(1, 1), (newId, 2)], "新班次分到 B 班;被删除的夜班(Id=2)不再有分配");
        assignments.Single(a => a.ShiftDefId == newId).AssignedByOperator.Should().Be("张班长");
    }

    /// <summary>迁移 v4:清理已删除(最后生效日已过)班次残留的班组分配,仍在生效的班次不受影响。</summary>
    [Fact]
    public async Task Migration_v4_removes_assignments_of_deleted_shifts()
    {
        await using (var conn = _factory.OpenWrite())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ShiftDefinition SET EffectiveTo = '2020-01-01' WHERE Id = 2;";
            await cmd.ExecuteNonQueryAsync();
        }

        var v4 = ProductionDashboard.Infrastructure.Persistence.Migrations.MigrationCatalog.All.Single(m => m.Version == 4);
        await using (var conn = _factory.OpenWrite())
        {
            foreach (var sql in v4.Statements)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        (await new TeamRepository(_factory).GetCurrentAssignmentsAsync(CancellationToken.None))
            .Select(a => a.ShiftDefId).Should().Equal(1);
    }

    /// <summary>
    /// 已结算的汇总不能被"进行中"的周期刷新覆盖(班次切换瞬间刷新可能晚于结算写入);再次结算仍可更新。
    /// </summary>
    [Fact]
    public async Task UpsertShiftSummary_in_progress_update_never_overwrites_a_settled_row()
    {
        var d = new DateOnly(2026, 5, 19);
        var start = d.ToDateTime(new TimeOnly(8, 0));
        var settled = new ShiftSummary(d, 1, 1, "白班", start, start.AddHours(12), 1500, 1200, 150, null);

        await _summaries.UpsertShiftSummaryAsync(settled, CancellationToken.None);
        await _summaries.UpsertShiftSummaryAsync(
            settled with { EndTime = null, ActualCount = 3, PeakHourCount = null }, CancellationToken.None);

        var row = await _summaries.GetShiftSummaryAsync(d, 1, 1, CancellationToken.None);
        row!.EndTime.Should().NotBeNull("进行中的刷新不能把已结算的行改回进行中");
        row.ActualCount.Should().Be(1200);

        await _summaries.UpsertShiftSummaryAsync(settled with { ActualCount = 1210 }, CancellationToken.None);
        (await _summaries.GetShiftSummaryAsync(d, 1, 1, CancellationToken.None))!.ActualCount.Should().Be(1210);
    }

    /// <summary>按时间区间统计每小时产量:只统计区间内的事件,按本地小时分桶。</summary>
    [Fact]
    public async Task GetHourlyCountsInRange_counts_only_events_inside_the_range()
    {
        var d = new DateOnly(2026, 5, 19);
        DateTime Local(int day, int hour, int minute) => d.AddDays(day).ToDateTime(new TimeOnly(hour, minute)).ToUniversalTime();
        await _events.InsertEventsAsync(
        [
            new ProductionEvent(0, Local(0, 19, 30), d, 1, 1),   // 区间前
            new ProductionEvent(0, Local(0, 20, 30), d, 2, 2),
            new ProductionEvent(0, Local(0, 23, 30), d, 2, 2),
            new ProductionEvent(0, Local(1, 1, 30), d, 2, 2),
            new ProductionEvent(0, Local(1, 8, 30), d, 1, 1),    // 区间后
        ], CancellationToken.None);

        var hourly = await _analytics.GetHourlyCountsInRangeAsync(Local(0, 20, 0), Local(1, 8, 0), CancellationToken.None);

        hourly.Sum().Should().Be(3);
        hourly[20].Should().Be(1);
        hourly[23].Should().Be(1);
        hourly[1].Should().Be(1);
    }

    /// <summary>
    /// ShiftDefinition 的 StartTime/EndTime(TimeOnly)经 DB("HH:mm" TEXT)往返应保真,
    /// 含非整点 + 跨日。验证 Repository 边界的 ToString/ParseExact 转换正确。
    /// </summary>
    [Fact]
    public async Task ShiftDefinition_roundtrips_TimeOnly_through_db()
    {
        var def = new ShiftDefinition(
            0, "X", "测试班",
            new TimeOnly(23, 30), new TimeOnly(7, 15),
            true, 999, "#123456", 9,
            new DateOnly(2026, 1, 1), null, DateTime.UtcNow);

        var id = await _shiftDefs.InsertShiftDefinitionAsync(def, CancellationToken.None);
        var loaded = await _shiftDefs.GetShiftDefinitionAsync(id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.StartTime.Should().Be(new TimeOnly(23, 30));
        loaded.EndTime.Should().Be(new TimeOnly(7, 15));
        loaded.CrossesMidnight.Should().BeTrue();
    }

    /// <summary>软删除写入"最后生效日",该日之后的查询不再返回它,之前的查询仍然返回(历史可追溯)。</summary>
    [Fact]
    public async Task SaveChanges_soft_delete_stops_shift_after_last_effective_day()
    {
        var today = new DateOnly(2026, 10, 11);

        await _shiftDefs.SaveChangesAsync(
            new ShiftDefinitionChanges(DeletedIds: [2], Added: [], Updated: []),
            deletedLastEffectiveDay: today.AddDays(-1),
            DateTime.UtcNow,
            CancellationToken.None);

        (await _shiftDefs.GetActiveShiftDefinitionsAsync(today, CancellationToken.None))
            .Select(s => s.Code).Should().Equal("A");
        (await _shiftDefs.GetActiveShiftDefinitionsAsync(today.AddDays(-1), CancellationToken.None))
            .Select(s => s.Code).Should().Equal("A", "B");
    }

    /// <summary>任何一步失败时整体回滚:删除、新增都不生效,重试不会重复新增。</summary>
    [Fact]
    public async Task SaveChanges_rolls_back_everything_when_one_step_fails()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var added = new ShiftDefinition(
            0, "C", "中班", new TimeOnly(16, 0), new TimeOnly(0, 0), true, 800, "#00ff88", 3,
            today, null, DateTime.UtcNow);
        var broken = (await _shiftDefs.GetShiftDefinitionAsync(1, CancellationToken.None))!
            with { ColorHex = null! };   // 违反 NOT NULL,模拟保存中途失败

        var save = () => _shiftDefs.SaveChangesAsync(
            new ShiftDefinitionChanges(DeletedIds: [2], Added: [added], Updated: [broken]),
            today.AddDays(-1),
            DateTime.UtcNow,
            CancellationToken.None);

        await save.Should().ThrowAsync<Exception>();

        var active = await _shiftDefs.GetActiveShiftDefinitionsAsync(today, CancellationToken.None);
        active.Select(s => s.Code).Should().Equal(["A", "B"], "删除和新增都应随失败一起回滚");
    }
}
