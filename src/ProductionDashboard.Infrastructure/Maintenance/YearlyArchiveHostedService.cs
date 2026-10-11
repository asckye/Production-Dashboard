using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Export;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Export;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Maintenance;

/// <summary>
/// 年度归档:每年 1 月 1 日(或启动时如果发现上一年还没归档)
/// 把上一年全部数据导出成 <c>Data/Archives/{年}.xlsx</c>,然后清掉主库里那段数据。
///
/// <para>
/// 同一年可能被归档不止一次(例如上次清库中途被中断,或之后又补进了该年的数据)。
/// 每次归档的 Excel 和清库前备份都另存为新文件,绝不覆盖之前的归档:
/// 否则第二次只含少量剩余数据的归档会把第一次完整的归档冲掉,数据就永久丢了。
/// </para>
/// </summary>
public sealed class YearlyArchiveHostedService : IHostedService, IAsyncDisposable
{
    /// <summary>主循环检查周期 — 一天一次足矣,过年那一天命中就行。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IShiftSummaryRepository _summaries;
    private readonly IProductionAnalyticsRepository _analytics;
    private readonly IDataLifecycleRepository _lifecycle;
    private readonly IExcelExportService _excel;
    private readonly IDbConnectionFactory _factory;
    private readonly IAuditLogger _audit;
    private readonly ILogger<YearlyArchiveHostedService> _logger;
    private readonly TimeProvider _clock;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _stopped;

    /// <summary>注入仓库 / 导出 / 审计 / 日志,<paramref name="clock"/> 可注入测试时钟。</summary>
    public YearlyArchiveHostedService(
        IShiftSummaryRepository summaries,
        IProductionAnalyticsRepository analytics,
        IDataLifecycleRepository lifecycle,
        IExcelExportService excel,
        IDbConnectionFactory factory,
        IAuditLogger audit,
        ILogger<YearlyArchiveHostedService> logger,
        TimeProvider? clock = null)
    {
        _summaries = summaries;
        _analytics = analytics;
        _lifecycle = lifecycle;
        _excel = excel;
        _factory = factory;
        _audit = audit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>启动 — 起后台循环,每 6 小时检查一次是否需要归档。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("YearlyArchive 启动 · 巡检周期 {Interval}", CheckInterval);
        return Task.CompletedTask;
    }

    /// <summary>停止 — 取消后台循环,等待优雅退出,幂等。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;
        if (_cts is null) return;

        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _logger.LogInformation("YearlyArchive 停止");
    }

    /// <summary>异步释放 — 委派给 StopAsync,并 dispose CTS。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        var cts = _cts;
        _cts = null;
        cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // 启动期立刻检查一次 — 万一上次启动没跨过年但今天跨了
        try { await TryArchiveExpiredYearsAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogError(ex, "YearlyArchive 启动期检查异常"); }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { await TryArchiveExpiredYearsAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "YearlyArchive 巡检异常");
            }
        }
    }

    private async Task TryArchiveExpiredYearsAsync(CancellationToken ct)
    {
        var currentYear = _clock.GetLocalNow().Year;

        // 看主库里最早的一条 ShiftSummary 是哪年 — 那年到 currentYear-1 都需要归档
        var firstYear = await GetEarliestShiftYearAsync(ct).ConfigureAwait(false);
        if (firstYear is null) return;       // 主库还没数据,无需归档
        if (firstYear.Value >= currentYear) return;   // 全是今年的数据,无需归档

        for (var year = firstYear.Value; year < currentYear; year++)
        {
            await ArchiveYearAsync(year, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 归档单个年度:导出 Excel → 验证 Excel → <b>清库前强制整库热备份</b> → 验证备份 → 清库。
    /// 任一保护失败都抛异常拒绝清库,确保"唯一永久删数据的路径"绝不在数据未安全落盘时执行。
    /// (internal 以便单元测试直接驱动,见 YearlyArchiveTests。)
    /// </summary>
    internal async Task ArchiveYearAsync(int year, CancellationToken ct)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);

        var summaries = await _summaries.GetShiftSummariesInRangeAsync(from, to, ct).ConfigureAwait(false);
        var dailies = await _analytics.GetDailyCountsAsync(from, to, ct).ConfigureAwait(false);

        if (summaries.Count == 0 && dailies.All(d => d.Count == 0))
        {
            _logger.LogInformation("{Year} 年无数据,跳过归档", year);
            return;
        }

        // 1) 导出 Excel
        var path = await _excel.ExportYearAsync(year, summaries, dailies, ct).ConfigureAwait(false);

        // 2) 验证 Excel 确实写成功(存在且非空),否则拒绝清库
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(
                $"{year} 年度归档 Excel 未生成或为空: {path},拒绝清库");
        }

        // 3) 清库前强制整库热备份 —— 双保险。Excel 是给人看的报表,可能列丢/格式坏却仍非空;
        //    .db 备份是原始数据的逐字节副本,万一 Excel 不可信也能从备份恢复。
        string backupPath;
        try
        {
            backupPath = await Task.Run(() => BackupBeforePurge(year), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{year} 年度归档清库前热备份失败,拒绝清库(原始数据未删,安全)", ex);
        }

        // 4) 验证备份文件确实落盘,否则拒绝清库
        if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
        {
            throw new InvalidOperationException(
                $"{year} 年度归档备份文件未生成或为空: {backupPath},拒绝清库");
        }

        // 5) 全部保护通过,清库
        var deleted = await _lifecycle.DeleteProductionDataInRangeAsync(from, to, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "{Year} 年度归档完成 · Excel {Excel} · 整库备份 {Backup} · 清掉 {Rows} 行(events + minutes + summaries)",
            year, Path.GetFileName(path), Path.GetFileName(backupPath), deleted);

        // 审计只记文件名,不记完整路径(避免泄露部署目录,呼应安全 MED-3)
        await _audit.WriteAsync(
            category: "YearlyArchive",
            detail: $"year={year}, excel={Path.GetFileName(path)}, backup={Path.GetFileName(backupPath)}, deletedRows={deleted}, summaries={summaries.Count}",
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清库前对整库做一次 SQLite Online Backup,落到 <c>Archives/pre-purge-{year}.db</c>
    /// (已存在时另存为带时间戳的新文件,不覆盖之前的备份)。
    /// 用只读连接作源,WAL 兼容、不阻塞写入。返回备份文件路径。
    /// </summary>
    private string BackupBeforePurge(int year)
    {
        var dbPath = _factory.DatabasePath;
        var archiveDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "Archives");
        Directory.CreateDirectory(archiveDir);
        var backupPath = UniqueFilePath.Next(archiveDir, $"pre-purge-{year}", ".db", _clock.GetLocalNow().DateTime);

        using var source = _factory.OpenRead();
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,   // 备份目标用完即弃,不入连接池(否则句柄滞留,下次覆盖删不掉)
        }.ToString());
        dest.Open();
        source.BackupDatabase(dest);
        return backupPath;
    }

    /// <summary>查主库最早一条 ShiftSummary 的年份。无数据时返回 null。</summary>
    private async Task<int?> GetEarliestShiftYearAsync(CancellationToken ct)
    {
        const string sql = "SELECT MIN(ShiftDate) FROM ShiftSummary;";
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull) return null;
        if (DateOnly.TryParse(result.ToString(), out var d)) return d.Year;
        // 解析失败不能静默返回 null(会被当成"无数据"永久跳过归档,老数据无限滞留主库)。
        _logger.LogWarning(
            "ShiftSummary.MIN(ShiftDate) 无法解析为日期: '{Raw}',本轮跳过归档检查,请检查数据格式", result);
        return null;
    }
}
