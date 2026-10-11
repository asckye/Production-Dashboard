using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Maintenance;

/// <summary>
/// 数据库维护后台任务:
/// <list type="bullet">
///   <item>每天 <see cref="DatabaseOptions.VacuumTime"/>(默认 02:00)整理数据库:WAL checkpoint,
///         空闲页较多时再做 VACUUM</item>
///   <item>每天 <see cref="DatabaseOptions.BackupTime"/> 热备份到 <c>Backups/yyyy-MM-dd.db</c>(默认 03:00)</item>
///   <item>清理超过 <see cref="DatabaseOptions.BackupRetentionDays"/> 天的旧备份</item>
/// </list>
/// 用每分钟轮询 + "最后执行日期" 的简单调度,容错 sleep/时钟跳变。
/// "最后执行日期"存在数据库的 AppConfig 表里,程序重启后不会在同一天重复执行。
/// <para>
/// VACUUM 期间整个数据库被独占锁住,计数写入只能等待,所以只在计划时刻之后的
/// <see cref="VacuumWindow"/> 内、且空闲页超过 <see cref="VacuumFreePageRatio"/> 时才做
/// (主库基本只增不删,通常只有年度归档清库之后才值得 VACUUM)。
/// </para>
/// </summary>
public sealed class DatabaseMaintenanceHostedService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>只在计划时刻之后这段时间内做 VACUUM;程序白天重启时不会在生产期间锁库。</summary>
    internal static readonly TimeSpan VacuumWindow = TimeSpan.FromHours(2);

    /// <summary>空闲页占比超过这个值才做 VACUUM。</summary>
    internal const double VacuumFreePageRatio = 0.25;

    private const string LastVacuumKey = "Maintenance.LastVacuumDate";
    private const string LastBackupKey = "Maintenance.LastBackupDate";

    private readonly IDbConnectionFactory _factory;
    private readonly DatabaseOptions _options;
    private readonly ILogger<DatabaseMaintenanceHostedService> _logger;
    private readonly TimeProvider _clock;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private DateOnly _lastVacuumDate = DateOnly.MinValue;
    private DateOnly _lastBackupDate = DateOnly.MinValue;
    private bool _lastDatesLoaded;

    /// <summary>注入连接工厂 / 选项 / 日志,<paramref name="clock"/> 可注入测试时钟。</summary>
    public DatabaseMaintenanceHostedService(
        IDbConnectionFactory factory,
        IOptions<DatabaseOptions> options,
        ILogger<DatabaseMaintenanceHostedService> logger,
        TimeProvider? clock = null)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>启动 — 起后台循环,每分钟检查是否到达 VACUUM / 备份时刻。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation(
            "DatabaseMaintenance 启动 · VACUUM 时间 {Vacuum} · 备份时间 {Backup} · 保留 {Days} 天",
            _options.VacuumTime, _options.BackupTime, _options.BackupRetentionDays);

        return Task.CompletedTask;
    }

    /// <summary>停止 — 取消后台循环,等待优雅退出,幂等。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }    // 已 dispose,直接结束

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _logger.LogInformation("DatabaseMaintenance 停止");
    }

    /// <summary>异步释放 — 委派给 StopAsync,并 dispose CTS。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        var cts = _cts;
        _cts = null;
        cts?.Dispose();
    }

    // ── 调度循环 ────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // 启动后等 1 分钟再开始检查,避开应用启动期数据库竞争
        try
        {
            await Task.Delay(CheckInterval, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库维护循环异常");
            }

            try { await Task.Delay(CheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>检查是否到了维护时刻并执行。internal 以便单元测试直接驱动。</summary>
    internal async Task CheckAndRunAsync(CancellationToken ct)
    {
        if (!_lastDatesLoaded)
        {
            _lastVacuumDate = await ReadDateAsync(LastVacuumKey, ct).ConfigureAwait(false);
            _lastBackupDate = await ReadDateAsync(LastBackupKey, ct).ConfigureAwait(false);
            _lastDatesLoaded = true;
        }

        var nowLocal = _clock.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(nowLocal);
        var nowTime = TimeOnly.FromDateTime(nowLocal);

        var vacuumTime = TimeOnly.Parse(_options.VacuumTime, CultureInfo.InvariantCulture);
        if (_lastVacuumDate < today && nowTime >= vacuumTime)
        {
            if (nowTime - vacuumTime <= VacuumWindow)
            {
                await RunVacuumAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "已过今天的数据库整理时段({Time} 起 {Window} 内),跳过,明天再做", vacuumTime, VacuumWindow);
            }
            _lastVacuumDate = today;
            await SaveDateAsync(LastVacuumKey, today, ct).ConfigureAwait(false);
        }

        var backupTime = TimeOnly.Parse(_options.BackupTime, CultureInfo.InvariantCulture);
        if (_lastBackupDate < today && nowTime >= backupTime)
        {
            await RunBackupAsync(today, ct).ConfigureAwait(false);
            _lastBackupDate = today;
            await SaveDateAsync(LastBackupKey, today, ct).ConfigureAwait(false);
        }
    }

    private async Task<DateOnly> ReadDateAsync(string key, CancellationToken ct)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppConfig WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.MinValue;
    }

    private async Task SaveDateAsync(string key, DateOnly date, CancellationToken ct)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppConfig (Key, Value, UpdatedAt) VALUES ($key, $value, $now)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$now", _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── VACUUM ──────────────────────────────────────────────────────

    private async Task RunVacuumAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = _factory.OpenWrite();

            // 先 checkpoint(TRUNCATE):把 WAL 已提交内容并回主库并清空 -wal 文件,
            // 防止长期 7×24 运行 WAL 无界增长(SQLite 自动 checkpoint 在有长读事务时可能迟迟不截断)。
            await CheckpointAsync(conn, ct).ConfigureAwait(false);

            var pageCount = await ScalarAsync(conn, "PRAGMA page_count;", ct).ConfigureAwait(false);
            var freePages = await ScalarAsync(conn, "PRAGMA freelist_count;", ct).ConfigureAwait(false);
            var freeRatio = pageCount == 0 ? 0 : (double)freePages / pageCount;
            if (freeRatio < VacuumFreePageRatio)
            {
                _logger.LogInformation(
                    "数据库整理 · WAL checkpoint 完成,空闲页 {Ratio:P0} 低于 {Threshold:P0},无需 VACUUM",
                    freeRatio, VacuumFreePageRatio);
                return;
            }

            // VACUUM 会复制整个数据库:临时数据写磁盘,不要放内存(连接默认 temp_store=MEMORY,大库会占用大量内存)。
            // 连接工厂每次取连接都会重新设置 temp_store,这里改了不影响其它连接。
            await using (var tempStore = conn.CreateCommand())
            {
                tempStore.CommandText = "PRAGMA temp_store=FILE;";
                await tempStore.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "VACUUM;";
                cmd.CommandTimeout = 600; // 10 分钟,大库 VACUUM 可能慢
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // WAL 模式下 VACUUM 把整个库写进 WAL,再 checkpoint 一次把 -wal 文件截断
            await CheckpointAsync(conn, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "VACUUM 完成 · 回收空闲页 {Ratio:P0} · 耗时 {Elapsed}", freeRatio, sw.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VACUUM 失败 · 耗时 {Elapsed}", sw.Elapsed);
        }
    }

    private static async Task CheckpointAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var checkpoint = conn.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.CommandTimeout = 120;
        await checkpoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    // ── 备份 + 清理 ─────────────────────────────────────────────────

    private async Task RunBackupAsync(DateOnly date, CancellationToken ct)
    {
        try
        {
            var backupDir = Path.GetFullPath(_options.BackupPath);
            Directory.CreateDirectory(backupDir);

            var backupFile = Path.Combine(backupDir, $"production-{date:yyyy-MM-dd}.db");

            // 用 SqliteConnection.BackupDatabase (Online Backup API) 做热备份,
            // 不需要停其他连接,WAL 数据也会一并复制。
            await using var source = _factory.OpenWrite();
            await using var dest = new SqliteConnection(
                $"Data Source={backupFile};Mode=ReadWriteCreate");
            dest.Open();
            source.BackupDatabase(dest);

            var size = new FileInfo(backupFile).Length;
            _logger.LogInformation(
                "备份完成 · {File} · {Size:N0} bytes", backupFile, size);

            await Task.Run(() => CleanupOldBackups(backupDir, date), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "备份失败");
        }
    }

    private void CleanupOldBackups(string backupDir, DateOnly today)
    {
        try
        {
            var cutoff = today.AddDays(-_options.BackupRetentionDays);
            var deleted = 0;

            foreach (var file in Directory.EnumerateFiles(backupDir, "production-*.db"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                // production-2026-05-19
                var parts = name.Split('-', 4);
                if (parts.Length != 4) continue;
                if (!DateOnly.TryParseExact(
                        string.Join('-', parts[1], parts[2], parts[3]),
                        "yyyy-MM-dd", null, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                if (date < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "清理过期备份 · 删除 {Count} 个早于 {Cutoff:yyyy-MM-dd} 的文件",
                    deleted, cutoff);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理旧备份失败");
        }
    }
}
