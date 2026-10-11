using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Configuration;
using ProductionDashboard.Services.UI;

namespace ProductionDashboard.Watchdog;

/// <summary>
/// 启动期 fork 一个 <c>--watchdog ...</c> 子进程,优雅退出时主动 kill 它,
/// 保证用户按 Esc 退出后任务管理器里不留进程残骸。
///
/// <para>看门狗的"自动重启"语义只在主程序 <b>崩溃</b>(被 OS 杀掉 / Stack Overflow / 心跳 hang)
/// 时生效;此时主程序不会跑到 <see cref="StopAsync"/>。</para>
///
/// <para>用户主动退出场景:Host 进入正常 Shutdown 流程 → 本服务的 StopAsync 杀掉
/// 看门狗 → 看门狗进程立即消失 → 主程序也随之退出 → 任务管理器 0 残留。</para>
/// </summary>
public sealed class WatchdogLauncherHostedService : IHostedService
{
    private readonly WatchdogOptions _options;
    private readonly UiHeartbeatHostedService _heartbeat;
    private readonly ILogger<WatchdogLauncherHostedService> _logger;

    private Process? _watchdogProcess;

    /// <summary>注入选项 / 心跳服务(用于拿心跳文件路径)/ 日志。</summary>
    public WatchdogLauncherHostedService(
        IOptions<WatchdogOptions> options,
        UiHeartbeatHostedService heartbeat,
        ILogger<WatchdogLauncherHostedService> logger)
    {
        _options = options.Value;
        _heartbeat = heartbeat;
        _logger = logger;
    }

    /// <summary>启动 — fork 子进程跑 watchdog 模式;Watchdog:Enabled=false 时跳过。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("看门狗未启用 (Watchdog:Enabled=false),跳过子进程 fork");
            return Task.CompletedTask;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogWarning("无法获取当前 exe 路径,看门狗未启动");
            return Task.CompletedTask;
        }

        var pid = Process.GetCurrentProcess().Id;
        var heartbeatPath = _heartbeat.HeartbeatAbsolutePath;

        var args = new[]
        {
            "--watchdog",
            $"--pid={pid.ToString(CultureInfo.InvariantCulture)}",
            $"--heartbeat={heartbeatPath}",
            $"--restart-exe={exePath}",
            $"--timeout-ms={_options.HeartbeatTimeoutMs.ToString(CultureInfo.InvariantCulture)}",
            $"--poll-ms={_options.PollIntervalMs.ToString(CultureInfo.InvariantCulture)}",
            $"--restart-delay-ms={_options.RestartDelayMs.ToString(CultureInfo.InvariantCulture)}",
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            _watchdogProcess = Process.Start(psi);
            _logger.LogInformation(
                "看门狗已 fork · pid={WatchdogPid} · timeout={Timeout}ms · poll={Poll}ms",
                _watchdogProcess?.Id, _options.HeartbeatTimeoutMs, _options.PollIntervalMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动看门狗子进程失败,主程序继续运行但无自动重启");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 优雅退出时主动 kill 看门狗。
    /// 看门狗作为"父进程崩溃后自动恢复"用,用户主动退出时没必要让它再跑、再轮询、再重启 — 直接干掉。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watchdogProcess is null) return Task.CompletedTask;

        try
        {
            if (!_watchdogProcess.HasExited)
            {
                var pid = _watchdogProcess.Id;
                _watchdogProcess.Kill(entireProcessTree: true);
                _watchdogProcess.WaitForExit(2000);
                _logger.LogInformation("看门狗已终止 · pid={WatchdogPid}", pid);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程对象已无效(可能已退出),忽略
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "终止看门狗失败,可能需要任务管理器手动结束");
        }
        finally
        {
            _watchdogProcess?.Dispose();
            _watchdogProcess = null;
        }

        return Task.CompletedTask;
    }
}
