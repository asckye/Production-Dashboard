using System.IO;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Export;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Export;

/// <summary>
/// 用 ClosedXML 生成 .xlsx。
/// 文件路径基于 <see cref="IDbConnectionFactory.DatabasePath"/> 所在目录的 <c>Exports/</c>、<c>Archives/</c> 子目录。
/// </summary>
public sealed class ClosedXmlExportService : IExcelExportService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<ClosedXmlExportService> _logger;

    /// <summary>注入连接工厂(用于定位 DataDir)+ 日志。</summary>
    public ClosedXmlExportService(
        IDbConnectionFactory factory,
        ILogger<ClosedXmlExportService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Data/ 目录(数据库文件所在目录)。</summary>
    private string DataDir => Path.GetDirectoryName(_factory.DatabasePath) ?? AppContext.BaseDirectory;

    /// <inheritdoc/>
    public async Task<string> ExportShiftAsync(
        ShiftSummary summary,
        IReadOnlyList<int> hourlyCounts,
        CancellationToken cancellationToken)
    {
        var yearDir = Path.Combine(DataDir, "Exports", summary.ShiftDate.Year.ToString());
        Directory.CreateDirectory(yearDir);

        var fileName = SanitizeFileName(
            $"{summary.ShiftDate:yyyy-MM-dd}_{summary.ShiftDefId}_{summary.ShiftName}.xlsx");
        var path = Path.Combine(yearDir, fileName);

        await Task.Run(() =>
        {
            using var wb = new XLWorkbook();

            // Sheet 1: 班次摘要
            var s1 = wb.Worksheets.Add("班次摘要");
            WriteShiftSummarySheet(s1, summary);

            // Sheet 2: 每小时产量
            var s2 = wb.Worksheets.Add("每小时产量");
            WriteHourlyCountsSheet(s2, hourlyCounts);

            wb.SaveAs(path);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("班次 Excel 已导出 · {Path}", path);
        return path;
    }

    /// <inheritdoc/>
    public async Task<string> ExportYearAsync(
        int year,
        IReadOnlyList<ShiftSummary> allSummaries,
        IReadOnlyList<DailyCount> dailyCounts,
        CancellationToken cancellationToken)
    {
        var archiveDir = Path.Combine(DataDir, "Archives");
        Directory.CreateDirectory(archiveDir);
        var path = Path.Combine(archiveDir, $"{year}.xlsx");

        await Task.Run(() =>
        {
            using var wb = new XLWorkbook();

            // Sheet 1: 年度摘要
            var s1 = wb.Worksheets.Add("年度摘要");
            WriteYearOverviewSheet(s1, year, allSummaries, dailyCounts);

            // Sheet 2: 班次汇总(所有)
            var s2 = wb.Worksheets.Add("班次汇总");
            WriteShiftSummariesSheet(s2, allSummaries);

            // Sheet 3: 每日产量
            var s3 = wb.Worksheets.Add("每日产量");
            WriteDailyCountsSheet(s3, dailyCounts);

            // Sheet 4: 每月产量
            var s4 = wb.Worksheets.Add("每月产量");
            WriteMonthlyCountsSheet(s4, dailyCounts);

            wb.SaveAs(path);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "{Year} 年度归档 Excel 已导出 · {Path} · {Count} 个班次 · {Days} 天数据",
            year, path, allSummaries.Count, dailyCounts.Count);
        return path;
    }

    // ── Sheet writers ────────────────────────────────────────────────

    private static void WriteShiftSummarySheet(IXLWorksheet s, ShiftSummary summary)
    {
        s.Cell("A1").Value = "班次摘要";
        s.Cell("A1").Style.Font.Bold = true;
        s.Cell("A1").Style.Font.FontSize = 16;
        s.Range("A1:B1").Merge();

        s.Cell("A3").Value = "班次日期";
        s.Cell("B3").Value = summary.ShiftDate.ToString("yyyy-MM-dd");

        s.Cell("A4").Value = "班次代号";
        s.Cell("B4").Value = summary.ShiftDefId;

        s.Cell("A5").Value = "班次名称";
        s.Cell("B5").Value = summary.ShiftName;

        s.Cell("A6").Value = "开始时间";
        s.Cell("B6").Value = summary.StartTime.ToString("yyyy-MM-dd HH:mm:ss");

        s.Cell("A7").Value = "结束时间";
        s.Cell("B7").Value = summary.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(进行中)";

        s.Cell("A8").Value = "目标产量";
        s.Cell("B8").Value = summary.TargetCount;

        s.Cell("A9").Value = "实际产量";
        s.Cell("B9").Value = summary.ActualCount;

        s.Cell("A10").Value = "完成率";
        var rate = summary.TargetCount > 0
            ? summary.ActualCount * 100.0 / summary.TargetCount
            : 0;
        s.Cell("B10").Value = $"{rate:F1}%";

        s.Cell("A11").Value = "峰值小时产量";
        if (summary.PeakHourCount.HasValue)
        {
            s.Cell("B11").Value = summary.PeakHourCount.Value;
        }
        else
        {
            s.Cell("B11").Value = "—";
        }

        s.Cell("A12").Value = "备注";
        s.Cell("B12").Value = summary.Remark ?? "";

        s.Range("A3:A12").Style.Font.Bold = true;
        s.Columns().AdjustToContents();
    }

    private static void WriteHourlyCountsSheet(IXLWorksheet s, IReadOnlyList<int> hourly)
    {
        s.Cell("A1").Value = "小时";
        s.Cell("B1").Value = "产量(件)";
        s.Range("A1:B1").Style.Font.Bold = true;

        for (var i = 0; i < hourly.Count; i++)
        {
            s.Cell(i + 2, 1).Value = $"{i:D2}:00";
            s.Cell(i + 2, 2).Value = hourly[i];
        }
        s.Columns().AdjustToContents();
    }

    private static void WriteYearOverviewSheet(
        IXLWorksheet s, int year,
        IReadOnlyList<ShiftSummary> summaries,
        IReadOnlyList<DailyCount> dailies)
    {
        s.Cell("A1").Value = $"{year} 年度生产数据归档";
        s.Cell("A1").Style.Font.Bold = true;
        s.Cell("A1").Style.Font.FontSize = 16;
        s.Range("A1:B1").Merge();

        var totalActual = summaries.Sum(x => x.ActualCount);
        var totalTarget = summaries.Sum(x => x.TargetCount);
        var rate = totalTarget > 0 ? totalActual * 100.0 / totalTarget : 0;
        var peakDay = dailies.Count > 0 ? dailies.MaxBy(d => d.Count) : null;
        var workingDays = dailies.Count(d => d.Count > 0);
        var peakHour = summaries.Where(x => x.PeakHourCount.HasValue)
            .DefaultIfEmpty(null)
            .Max(x => x?.PeakHourCount ?? 0);

        var rows = new (string Label, object Value)[]
        {
            ("年度",        year),
            ("班次总数",    summaries.Count),
            ("有产量天数",  workingDays),
            ("总目标产量",  totalTarget),
            ("总实际产量",  totalActual),
            ("总完成率",    $"{rate:F2}%"),
            ("最高单日",    peakDay is null ? "—" : $"{peakDay.Date:yyyy-MM-dd} ({peakDay.Count} 件)"),
            ("最高小时",    peakHour > 0 ? $"{peakHour} 件" : "—"),
            ("导出时间",    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
        };

        for (var i = 0; i < rows.Length; i++)
        {
            s.Cell(i + 3, 1).Value = rows[i].Label;
            s.Cell(i + 3, 2).Value = XLCellValue.FromObject(rows[i].Value);
        }

        s.Range($"A3:A{rows.Length + 2}").Style.Font.Bold = true;
        s.Columns().AdjustToContents();
    }

    private static void WriteShiftSummariesSheet(IXLWorksheet s, IReadOnlyList<ShiftSummary> summaries)
    {
        var headers = new[]
        {
            "班次日期", "班次代号", "班次名称",
            "开始时间", "结束时间", "目标产量", "实际产量",
            "完成率(%)", "峰值小时", "备注",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            s.Cell(1, i + 1).Value = headers[i];
        }
        s.Row(1).Style.Font.Bold = true;

        var ordered = summaries.OrderBy(x => x.ShiftDate).ThenBy(x => x.ShiftDefId).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var x = ordered[i];
            var rate = x.TargetCount > 0 ? x.ActualCount * 100.0 / x.TargetCount : 0;
            var r = i + 2;
            s.Cell(r, 1).Value = x.ShiftDate.ToString("yyyy-MM-dd");
            s.Cell(r, 2).Value = x.ShiftDefId;
            s.Cell(r, 3).Value = x.ShiftName;
            s.Cell(r, 4).Value = x.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
            s.Cell(r, 5).Value = x.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            s.Cell(r, 6).Value = x.TargetCount;
            s.Cell(r, 7).Value = x.ActualCount;
            s.Cell(r, 8).Value = Math.Round(rate, 2);
            s.Cell(r, 9).Value = x.PeakHourCount.HasValue ? (XLCellValue)x.PeakHourCount.Value : Blank.Value;
            s.Cell(r, 10).Value = x.Remark ?? "";
        }
        s.Columns().AdjustToContents();
        s.SheetView.FreezeRows(1);
    }

    private static void WriteDailyCountsSheet(IXLWorksheet s, IReadOnlyList<DailyCount> dailies)
    {
        s.Cell("A1").Value = "日期";
        s.Cell("B1").Value = "产量(件)";
        s.Range("A1:B1").Style.Font.Bold = true;

        var ordered = dailies.OrderBy(d => d.Date).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            s.Cell(i + 2, 1).Value = ordered[i].Date.ToString("yyyy-MM-dd");
            s.Cell(i + 2, 2).Value = ordered[i].Count;
        }
        s.Columns().AdjustToContents();
        s.SheetView.FreezeRows(1);
    }

    private static void WriteMonthlyCountsSheet(IXLWorksheet s, IReadOnlyList<DailyCount> dailies)
    {
        s.Cell("A1").Value = "月份";
        s.Cell("B1").Value = "产量(件)";
        s.Range("A1:B1").Style.Font.Bold = true;

        var grouped = dailies
            .GroupBy(d => new { d.Date.Year, d.Date.Month })
            .Select(g => new { Month = $"{g.Key.Year}-{g.Key.Month:D2}", Total = g.Sum(d => d.Count) })
            .OrderBy(x => x.Month)
            .ToList();

        for (var i = 0; i < grouped.Count; i++)
        {
            s.Cell(i + 2, 1).Value = grouped[i].Month;
            s.Cell(i + 2, 2).Value = grouped[i].Total;
        }
        s.Columns().AdjustToContents();
        s.SheetView.FreezeRows(1);
    }

    /// <summary>清掉文件名里不允许的字符。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
