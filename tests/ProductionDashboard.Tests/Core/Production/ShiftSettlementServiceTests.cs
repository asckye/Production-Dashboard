using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Export;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;

namespace ProductionDashboard.Tests.Core.Production;

/// <summary>
/// ShiftSettlementService 的集成测试。该服务从 AggregationHostedService 拆出后获得独立可测性,
/// 覆盖审查指出的零测试盲区:多班组各写一行 ShiftSummary、Excel 导出失败不影响结算。
/// </summary>
public sealed class ShiftSettlementServiceTests : IAsyncLifetime
{
    private const int TeamA = 1; // 种子 A 班
    private const int TeamB = 2; // 种子 B 班

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdss_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private ProductionEventRepository _events = null!;
    private ProductionAnalyticsRepository _analytics = null!;
    private ShiftSummaryRepository _summaries = null!;
    private StubExcel _excel = null!;
    private ShiftSettlementService _sut = null!;

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
        _summaries = new ShiftSummaryRepository(_factory);
        _excel = new StubExcel();
        _sut = new ShiftSettlementService(
            _summaries, _analytics, new ShiftDefinitionRepository(_factory), _excel,
            new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance),
            NullLogger<ShiftSettlementService>.Instance);
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

    // A 班 08:00-20:00(不跨日),ShiftDefId=1
    private static ShiftAssignment AShift(DateOnly date) => new(
        new ShiftDefinition(
            1, "A", "白班", new TimeOnly(8, 0), new TimeOnly(20, 0),
            false, 1500, "#ff8c42", 1, new DateOnly(1970, 1, 1), null, DateTime.UtcNow),
        date,
        date.ToDateTime(new TimeOnly(8, 0)),
        date.ToDateTime(new TimeOnly(20, 0)));

    private async Task SeedAsync(DateOnly date, int teamAEvents, int teamBEvents)
    {
        var noonUtc = date.ToDateTime(new TimeOnly(12, 0)).ToUniversalTime();
        var list = new List<ProductionEvent>();
        for (var i = 0; i < teamAEvents; i++)
            list.Add(new ProductionEvent(0, noonUtc.AddSeconds(i), date, 1, TeamA));
        for (var i = 0; i < teamBEvents; i++)
            list.Add(new ProductionEvent(0, noonUtc.AddSeconds(1000 + i), date, 1, TeamB));
        await _events.InsertEventsAsync(list, CancellationToken.None);
    }

    [Fact]
    public async Task SettleAsync_writes_one_summary_row_per_team_with_correct_counts()
    {
        var date = new DateOnly(2026, 5, 19);
        await SeedAsync(date, teamAEvents: 5, teamBEvents: 3);

        await _sut.SettleAsync(AShift(date), CancellationToken.None);

        var rows = await _summaries.GetShiftSummariesForShiftAsync(date, 1, CancellationToken.None);
        rows.Should().HaveCount(2, "A、B 两个班组各写一行");
        rows.Single(r => r.TeamId == TeamA).ActualCount.Should().Be(5);
        rows.Single(r => r.TeamId == TeamB).ActualCount.Should().Be(3);
        rows.Should().OnlyContain(r => r.EndTime != null, "结算写入的是终态(EndTime 非空)");
    }

    [Fact]
    public async Task SettleAsync_persists_summaries_even_when_excel_export_fails()
    {
        var date = new DateOnly(2026, 5, 19);
        await SeedAsync(date, teamAEvents: 4, teamBEvents: 0);
        _excel.ThrowOnExportShift = new IOException("模拟磁盘满");

        var act = async () => await _sut.SettleAsync(AShift(date), CancellationToken.None);

        await act.Should().NotThrowAsync("Excel 失败被内部吞掉,不影响结算");
        var rows = await _summaries.GetShiftSummariesForShiftAsync(date, 1, CancellationToken.None);
        rows.Should().ContainSingle(r => r.TeamId == TeamA)
            .Which.ActualCount.Should().Be(4, "Excel 导出失败时 ShiftSummary 仍已落库");
    }

    /// <summary>写一行"进行中"的汇总(周期刷新写的那种,EndTime 为空)。</summary>
    private Task WriteUnfinishedSummaryAsync(DateOnly date, int actual) =>
        _summaries.UpsertShiftSummaryAsync(
            new ShiftSummary(date, 1, TeamA, "白班", date.ToDateTime(new TimeOnly(8, 0)), null, 1500, actual, null, null),
            CancellationToken.None);

    [Fact]
    public async Task SettleUnfinished_settles_a_shift_that_ended_while_the_app_was_not_running()
    {
        var date = new DateOnly(2026, 5, 19);
        await SeedAsync(date, teamAEvents: 5, teamBEvents: 0);
        await WriteUnfinishedSummaryAsync(date, actual: 3);   // 停机前最后一次刷新时是 3 件

        await _sut.SettleUnfinishedAsync(current: null, CancellationToken.None);

        var row = (await _summaries.GetShiftSummariesForShiftAsync(date, 1, CancellationToken.None)).Single();
        row.EndTime.Should().Be(date.ToDateTime(new TimeOnly(20, 0)), "补结算后应写入结束时间");
        row.ActualCount.Should().Be(5, "按原始事件重新统计");
    }

    [Fact]
    public async Task SettleUnfinished_leaves_the_current_and_not_yet_ended_shifts_alone()
    {
        var past = new DateOnly(2026, 5, 19);
        var future = DateOnly.FromDateTime(DateTime.Now).AddDays(2);
        await WriteUnfinishedSummaryAsync(past, actual: 3);
        await WriteUnfinishedSummaryAsync(future, actual: 1);

        await _sut.SettleUnfinishedAsync(current: AShift(past), CancellationToken.None);

        (await _summaries.GetUnfinishedShiftSummariesAsync(CancellationToken.None))
            .Select(s => s.ShiftDate).Should().BeEquivalentTo([past, future], "正在进行的和还没结束的都不结算");
    }

    [Fact]
    public async Task Excel_hourly_counts_cover_only_the_shift_period()
    {
        // 夜班 20:00 → 次日 08:00(按结束日归属到 5/20);同一天白天的产量不能混进来
        var shiftDate = new DateOnly(2026, 5, 20);
        var night = new ShiftAssignment(
            new ShiftDefinition(2, "B", "夜班", new TimeOnly(20, 0), new TimeOnly(8, 0), true, 1200, "#4a9eff", 2,
                new DateOnly(1970, 1, 1), null, DateTime.UtcNow),
            shiftDate,
            new DateOnly(2026, 5, 19).ToDateTime(new TimeOnly(20, 0)),
            shiftDate.ToDateTime(new TimeOnly(8, 0)));
        var inShift = night.PeriodStart.AddHours(1).ToUniversalTime();          // 21:00
        var dayTime = shiftDate.ToDateTime(new TimeOnly(12, 0)).ToUniversalTime();
        await _events.InsertEventsAsync(
        [
            new ProductionEvent(0, inShift, shiftDate, 2, TeamB),
            new ProductionEvent(0, inShift.AddMinutes(1), shiftDate, 2, TeamB),
            new ProductionEvent(0, dayTime, shiftDate, 1, TeamA),
        ], CancellationToken.None);

        await _sut.SettleAsync(night, CancellationToken.None);

        _excel.LastHourly.Should().NotBeNull();
        _excel.LastHourly!.Sum().Should().Be(2, "只统计本班次时段内的 2 件");
        _excel.LastHourly[21].Should().Be(2);
    }

    private sealed class StubExcel : IExcelExportService
    {
        public Exception? ThrowOnExportShift { get; set; }
        public IReadOnlyList<int>? LastHourly { get; private set; }

        public Task<string> ExportShiftAsync(
            ShiftSummary summary, IReadOnlyList<int> hourlyCounts, CancellationToken cancellationToken)
        {
            if (ThrowOnExportShift is not null) throw ThrowOnExportShift;
            LastHourly = hourlyCounts;
            return Task.FromResult("stub-shift.xlsx");
        }

        public Task<string> ExportYearAsync(
            int year, IReadOnlyList<ShiftSummary> allSummaries,
            IReadOnlyList<DailyCount> dailyCounts, CancellationToken cancellationToken)
            => Task.FromResult("stub-year.xlsx");
    }
}
