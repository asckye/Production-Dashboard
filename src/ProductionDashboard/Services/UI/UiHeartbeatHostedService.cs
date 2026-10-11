using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Configuration;
using ProductionDashboard.Watchdog;

namespace ProductionDashboard.Services.UI;

/// <summary>
/// 在 UI 线程上周期性写入心跳文件。
/// 关键点:必须用 <see cref="DispatcherTimer"/> 而不是 <see cref="System.Threading.Timer"/>,
/// 否则 UI 死锁时心跳还能被后台线程刷新,守护就抓不到这种 hang。
/// </summary>
public sealed class UiHeartbeatHostedService : IHostedService, IAsyncDisposable
{
    private readonly WatchdogOptions _options;
    private readonly ILogger<UiHeartbeatHostedService> _logger;
    private readonly string _heartbeatPath;
    private readonly string? _version;

    private DispatcherTimer? _timer;
    private int _currentPid;
    private int _stopped;   // 0 = 运行中,1 = 已停止。本服务既作单例又作后台服务注册,StopAsync / DisposeAsync 可能被多次调用

    /// <summary>注入 WatchdogOptions 和日志,解析心跳文件绝对路径并取版本号。</summary>
    public UiHeartbeatHostedService(
        IOptions<WatchdogOptions> options,
        ILogger<UiHeartbeatHostedService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _heartbeatPath = ResolveHeartbeatPath(_options.HeartbeatPath);
        _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    }

    /// <summary>暴露给 WatchdogLauncher 用,避免两边自己拼路径走偏。</summary>
    public string HeartbeatAbsolutePath => _heartbeatPath;

    /// <summary>启动 — 立即写一次心跳,然后在 UI Dispatcher 上起 DispatcherTimer 定时刷新。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("UI 心跳已禁用 (Watchdog:Enabled=false)");
            return Task.CompletedTask;
        }

        _currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("UI 心跳必须在 WPF Application 启动后注册");

        // 立刻写一次,免得守护进程刚启动那几秒读不到
        WriteOnce(graceful: false);

        dispatcher.Invoke(() =>
        {
            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMs),
                DispatcherPriority.Background,
                OnTick,
                dispatcher);
            _timer.Start();
        });

        _logger.LogInformation(
            "UI 心跳启动 · 文件 {Path} · 间隔 {Interval}ms",
            _heartbeatPath, _options.HeartbeatIntervalMs);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 — 停 timer 并写一次 graceful=true,让守护进程知道是优雅退出不要重启。只执行一次。
    /// <para>
    /// 不能用 <c>Dispatcher.Invoke</c> 同步回到 UI 线程:退出时 UI 线程可能正在等待本方法,会互相等待而死锁。
    /// 先置停止标志让 OnTick 不再写心跳,再异步请求停止 timer。
    /// </para>
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return Task.CompletedTask;

        var timer = _timer;
        _timer = null;
        if (timer is not null)
        {
            // DispatcherTimer.Stop 必须在 Dispatcher 上执行
            if (timer.Dispatcher.CheckAccess()) timer.Stop();
            else timer.Dispatcher.BeginInvoke(timer.Stop);
        }

        // 主程序优雅退出 → 写 gracefulExit=true,守护看到就不再重启
        WriteOnce(graceful: true);
        _logger.LogInformation("UI 心跳停止 · 已置 gracefulExit");

        return Task.CompletedTask;
    }

    /// <summary>异步释放 — 委派给 StopAsync。</summary>
    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync(CancellationToken.None));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _stopped) == 1) return;   // 已停止:不要覆盖 graceful 标记
        WriteOnce(graceful: false);
    }

    private void WriteOnce(bool graceful)
    {
        try
        {
            var hb = new Heartbeat
            {
                Pid = _currentPid == 0 ? System.Diagnostics.Process.GetCurrentProcess().Id : _currentPid,
                Timestamp = DateTimeOffset.UtcNow,
                Version = _version,
                GracefulExit = graceful,
            };
            HeartbeatFile.Write(_heartbeatPath, hb);
        }
        catch (Exception ex)
        {
            // 心跳失败不能让 UI 崩,但需要告警。守护会在 timeout 后自动重启。
            _logger.LogWarning(ex, "写入 UI 心跳文件失败 · {Path}", _heartbeatPath);
        }
    }

    private static string ResolveHeartbeatPath(string configured)
    {
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }
}
