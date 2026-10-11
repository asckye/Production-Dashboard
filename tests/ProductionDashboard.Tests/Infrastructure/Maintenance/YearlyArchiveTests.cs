using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Export;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Maintenance;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;
using ProductionDashboard.Tests.Helpers;

namespace ProductionDashboard.Tests.Infrastructure.Maintenance;

/// <summary>
/// YearlyArchiveHostedService 的数据安全回归测试。这是全系统唯一会永久删除生产数据的路径,
/// 重点验证清库前的两道保护(Excel 非空 + 整库热备份)在任何一道失败时都拒绝清库。
/// 用真实 SQLite + 真实仓库 + 受控的 stub Excel 驱动 internal 的 ArchiveYearAsync。
/// </summary>
public sealed class YearlyArchiveTests : IAsyncLifetime
{
    // 每个测试独立目录:归档 / 备份文件写在数据库同级的 Archives/ 下,互不干扰
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"pdya_{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public YearlyArchiveTests()
    {
        _dbPath = Path.Combine(_dataDir, "production.db");
    }

    private string ArchiveDir => Path.Combine(_dataDir, "Archives");

    private SqliteConnectionFactory _factory = null!;
    private ShiftSummaryRepository _summaries = null!;
    private ProductionAnalyticsRepository _analytics = null!;
    private DataLifecycleRepository _lifecycle = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
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

        _summaries = new ShiftSummaryRepository(_factory);
        _analytics = new ProductionAnalyticsRepository(_factory);
        _lifecycle = new DataLifecycleRepository(_factory);
    }

    public Task DisposeAsync()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
        }
        catch { /* 清理失败不影响断言 */ }
        return Task.CompletedTask;
    }

    private YearlyArchiveHostedService MakeSut(Func<int, string> yearExporter)
        => new(
            _summaries, _analytics, _lifecycle,
            new StubExcel(yearExporter),
            _factory,
            new StubAudit(),
            NullLogger<YearlyArchiveHostedService>.Instance,
            new ControlledTimeProvider());

    private async Task SeedYearAsync(int year)
    {
        // ShiftDefId=1 / TeamId=1 是迁移种子(白班 / A 班),FK 有效。
        await _summaries.UpsertShiftSummaryAsync(
            new ShiftSummary(
                ShiftDate: new DateOnly(year, 6, 1),
                ShiftDefId: 1,
                TeamId: 1,
                ShiftName: "白班",
                StartTime: new DateOnly(year, 6, 1).ToDateTime(new TimeOnly(8, 0)),
                EndTime: new DateOnly(year, 6, 1).ToDateTime(new TimeOnly(20, 0)),
                TargetCount: 1500,
                ActualCount: 1200,
                PeakHourCount: 100,
                Remark: null),
            CancellationToken.None);
    }

    // ── T1:Excel 为空 → 拒绝清库 ──────────────────────────────────────

    [Fact]
    public async Task ArchiveYear_refuses_to_purge_when_excel_is_empty()
    {
        await SeedYearAsync(2025);

        // stub Excel 返回一个 0 字节文件(模拟磁盘满/ClosedXML 写一半失败)
        var emptyExcel = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.xlsx");
        await File.WriteAllBytesAsync(emptyExcel, Array.Empty<byte>());
        var sut = MakeSut(_ => emptyExcel);

        var act = async () => await sut.ArchiveYearAsync(2025, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("Excel 为空时必须拒绝清库");

        // 数据必须仍然在库
        var remaining = await _summaries.GetShiftSummariesInRangeAsync(
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), CancellationToken.None);
        remaining.Should().NotBeEmpty("Excel 验证失败时,生产数据绝不能被删除");

        File.Delete(emptyExcel);
    }

    // ── T2:正常归档 → 先备份再清库 + 幂等 ────────────────────────────

    [Fact]
    public async Task ArchiveYear_backs_up_before_purge_and_is_idempotent()
    {
        await SeedYearAsync(2025);

        var excelPath = Path.Combine(Path.GetTempPath(), $"year_{Guid.NewGuid():N}.xlsx");
        var sut = MakeSut(_ =>
        {
            File.WriteAllText(excelPath, "real-archive-content"); // 非空,通过验证
            return excelPath;
        });

        await sut.ArchiveYearAsync(2025, CancellationToken.None);

        // 1) 该年数据已清库
        var remaining = await _summaries.GetShiftSummariesInRangeAsync(
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), CancellationToken.None);
        remaining.Should().BeEmpty("归档成功后该年数据应已从主库清除");

        // 2) 清库前的整库热备份存在且非空(双保险)
        var backupPath = Path.Combine(ArchiveDir, "pre-purge-2025.db");
        File.Exists(backupPath).Should().BeTrue("清库前必须生成整库热备份");
        new FileInfo(backupPath).Length.Should().BeGreaterThan(0, "备份文件不能为空");

        // 3) 再次归档同一年 → 无数据,幂等跳过,不抛异常、不重复删
        var act = async () => await sut.ArchiveYearAsync(2025, CancellationToken.None);
        await act.Should().NotThrowAsync("重复归档已清空年份应幂等");

        File.Delete(excelPath);
    }

    // ── T3:同一年再次归档 → 另存新备份,不覆盖第一次的 ───────────────

    [Fact]
    public async Task Archiving_the_same_year_again_never_overwrites_the_earlier_backup()
    {
        var excelPath = Path.Combine(_dataDir, "year.xlsx");
        var sut = MakeSut(_ =>
        {
            File.WriteAllText(excelPath, "archive");
            return excelPath;
        });

        await SeedYearAsync(2025);
        await sut.ArchiveYearAsync(2025, CancellationToken.None);
        var first = Path.Combine(ArchiveDir, "pre-purge-2025.db");
        var firstBytes = await File.ReadAllBytesAsync(first);

        // 之后又补进了 2025 年的数据(例如上次清库被中断后残留),再次归档
        await SeedYearAsync(2025);
        await sut.ArchiveYearAsync(2025, CancellationToken.None);

        Directory.GetFiles(ArchiveDir, "pre-purge-2025*.db").Should().HaveCount(2, "第二次归档应另存新备份");
        (await File.ReadAllBytesAsync(first)).Should().Equal(firstBytes, "第一次的备份必须原样保留");
    }

    // ── stubs ──────────────────────────────────────────────────────────

    private sealed class StubExcel(Func<int, string> yearExporter) : IExcelExportService
    {
        public Task<string> ExportShiftAsync(
            ShiftSummary summary, IReadOnlyList<int> hourlyCounts, CancellationToken cancellationToken)
            => Task.FromResult("unused-in-archive-tests");

        public Task<string> ExportYearAsync(
            int year, IReadOnlyList<ShiftSummary> allSummaries,
            IReadOnlyList<DailyCount> dailyCounts, CancellationToken cancellationToken)
            => Task.FromResult(yearExporter(year));
    }

    private sealed class StubAudit : IAuditLogger
    {
        public Task WriteAsync(
            string category, string detail, string? user = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
