using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Export;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Tests.Infrastructure.Export;

/// <summary>班次 Excel 的"每小时产量"表:只列本班次覆盖的小时,并按时间顺序排列。</summary>
public sealed class ClosedXmlExportServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdxl_").FullName;

    private ClosedXmlExportService MakeSut() => new(
        new SqliteConnectionFactory(Options.Create(new DatabaseOptions
        {
            Path = Path.Combine(_dir, "production.db"),
            BackupPath = Path.Combine(_dir, "Backups"),
            VacuumTime = "02:00",
            BackupTime = "03:00",
        })),
        NullLogger<ClosedXmlExportService>.Instance);

    [Fact]
    public async Task Night_shift_hourly_sheet_runs_from_start_to_end_in_order()
    {
        var hourly = new int[24];
        hourly[20] = 7;
        hourly[3] = 9;
        var summary = new ShiftSummary(
            new DateOnly(2026, 5, 20), 2, 0, "夜班",
            new DateTime(2026, 5, 19, 20, 0, 0), new DateTime(2026, 5, 20, 8, 0, 0),
            1200, 16, 9, null);

        var path = await MakeSut().ExportShiftAsync(summary, hourly, CancellationToken.None);

        using var wb = new XLWorkbook(path);
        var sheet = wb.Worksheet("每小时产量");
        var rows = Enumerable.Range(2, 12)
            .Select(r => (Hour: sheet.Cell(r, 1).GetString(), Count: sheet.Cell(r, 2).GetValue<int>()))
            .ToList();
        rows.First().Should().Be(("20:00", 7));
        rows.Last().Hour.Should().Be("07:00");
        rows.Should().Contain(("03:00", 9));
        sheet.Cell(14, 1).IsEmpty().Should().BeTrue("12 小时的班次只有 12 行");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 尽力清理 */ }
    }
}
