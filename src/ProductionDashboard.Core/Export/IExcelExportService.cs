using ProductionDashboard.Core.Production;

namespace ProductionDashboard.Core.Export;

/// <summary>
/// 导出 Excel 文件的服务。
/// 班次结束生成单班 Excel,年度归档生成全年 Excel。
/// </summary>
public interface IExcelExportService
{
    /// <summary>
    /// 班次结束时调用,生成 <c>Data/Exports/{年}/{日期}_{班次代号}_{班次名}.xlsx</c>。
    /// 包含班次摘要 + 每小时柱状 + 完整事件清单。
    /// 返回生成的绝对路径,失败抛异常。
    /// </summary>
    Task<string> ExportShiftAsync(
        ShiftSummary summary,
        IReadOnlyList<int> hourlyCounts,
        CancellationToken cancellationToken);

    /// <summary>
    /// 年度归档时调用,生成 <c>Data/Archives/{年}.xlsx</c>;该文件已存在时另存为带时间戳的新文件,不覆盖。
    /// 包含年度摘要 + 所有班次汇总 + 每日产量 + 每月产量。
    /// 返回生成的绝对路径。
    /// </summary>
    Task<string> ExportYearAsync(
        int year,
        IReadOnlyList<ShiftSummary> allSummaries,
        IReadOnlyList<DailyCount> dailyCounts,
        CancellationToken cancellationToken);
}
