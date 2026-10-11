using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Maintenance;

/// <summary>
/// 定期巡检系统级风险:
/// <list type="bullet">
///   <item>系统时间倒退 (NTP 同步 / 手动改表)</item>
///   <item>数据库所在分区剩余空间</item>
/// </list>
/// 异常通过 <see cref="IGlobalExceptionHandler"/> 抛出,顶部红条会自动显示。
/// </summary>
public sealed class SystemHealthMonitor : BackgroundService
{
    private readonly SystemHealthOptions _options;
    private readonly IDbConnectionFactory _factory;
    private readonly IGlobalExceptionHandler _exceptions;
    private readonly ILogger<SystemHealthMonitor> _logger;
    private readonly TimeProvider _clock;

    private DateTimeOffset _lastCheckAt;
    private bool _diskAlerted;          // 防抖:不要每分钟刷一次同样的告警

    /// <summary>注入选项 / 连接工厂 / 全局异常 / 日志,<paramref name="clock"/> 可注入测试时钟。</summary>
    public SystemHealthMonitor(
        IOptions<SystemHealthOptions> options,
        IDbConnectionFactory factory,
        IGlobalExceptionHandler exceptions,
        ILogger<SystemHealthMonitor> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _factory = factory;
        _exceptions = exceptions;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SystemHealthMonitor 已禁用");
            return;
        }

        _lastCheckAt = _clock.GetUtcNow();
        var interval = TimeSpan.FromMilliseconds(_options.CheckIntervalMs);

        // 启动后等一个间隔再开始,避免和其他启动期检查抢资源
        try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckClock();
                CheckDisk();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SystemHealthMonitor 巡检异常");
            }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void CheckClock()
    {
        var now = _clock.GetUtcNow();
        var elapsed = now - _lastCheckAt;
        // 期望 elapsed ≈ 配置间隔,如果实际比期望小很多 (含负值) 说明时钟倒退
        var expected = TimeSpan.FromMilliseconds(_options.CheckIntervalMs);
        var regression = expected - elapsed;
        if (regression > TimeSpan.FromSeconds(_options.MaxClockRegressionSeconds))
        {
            _exceptions.Report(
                new InvalidOperationException(
                    $"系统时间倒退 {regression.TotalSeconds:F0} 秒 (期望 {expected.TotalSeconds:F0}s, 实际 {elapsed.TotalSeconds:F0}s)。" +
                    $" 检查 NTP 同步或手动改时间。统计聚合可能错位。"),
                "SystemHealth");
        }
        _lastCheckAt = now;
    }

    private void CheckDisk()
    {
        var path = _factory.DatabasePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
            if (freeMb < _options.MinFreeDiskMb)
            {
                if (!_diskAlerted)
                {
                    _exceptions.Report(
                        new InvalidOperationException(
                            $"磁盘 {drive.Name} 剩余空间 {freeMb:N0} MB 已低于阈值 {_options.MinFreeDiskMb:N0} MB。" +
                            $" 数据库 {path} 可能很快写不下,请清理。"),
                        "SystemHealth");
                    _diskAlerted = true;
                }
            }
            else if (_diskAlerted && freeMb > _options.MinFreeDiskMb * 1.5)
            {
                // 空间恢复(50% 余量),允许下次再告警
                _diskAlerted = false;
                _logger.LogInformation(
                    "磁盘空间已恢复 · {Drive} 剩余 {Free:N0} MB",
                    drive.Name, freeMb);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "磁盘空间检查失败 · {Path}", path);
        }
    }
}
