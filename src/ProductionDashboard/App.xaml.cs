using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Configuration;
using ProductionDashboard.Core.DependencyInjection;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Infrastructure.DependencyInjection;
using ProductionDashboard.Infrastructure.Plc;
using ProductionDashboard.Services.UI;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ProductionDashboard;

/// <summary>
/// WPF 应用程序根。持有 Generic <see cref="IHost"/>,使得配置、日志、依赖注入
/// 和后台 HostedService 全部聚拢在同一处生命周期管理。
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FallbackStopTimeout = TimeSpan.FromSeconds(5);

    private IHost? _host;
    private bool _shutdownStarted;

    /// <summary>应用启动入口 — 构建 Generic Host,挂全局异常,显示主窗口。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);

        // 日志 — Serilog 自身读取 appsettings.json 的 "Serilog" 节点完成配置
        builder.Services.AddSerilog((services, lc) => lc
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext());

        RegisterServices(builder.Services, builder.Configuration);

        _host = builder.Build();
        _host.Start();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation(
            "ProductionDashboard 启动 · 版本 {Version} · 环境 {Environment}",
            typeof(App).Assembly.GetName().Version,
            builder.Environment.EnvironmentName);

        // ── 全局异常捕获三路:必须在 host 启动后、MainWindow 显示前挂上 ──
        var exHandler = _host.Services.GetRequiredService<IGlobalExceptionHandler>();
        HookGlobalExceptionHandlers(exHandler, logger);

        // 主动解析 ThemeService 让构造器跑,这样若用户上次选了 Light,
        // MainWindow 还没显示时就已经把 Light.xaml 合并进资源
        _host.Services.GetRequiredService<IThemeService>();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// 把所有可能逃逸的异常路径都挂到 <paramref name="handler"/>。
    /// 三路覆盖:
    /// <list type="bullet">
    ///   <item><c>AppDomain.UnhandledException</c> — 非 UI 线程上未捕获的异常(致命,进程将退出)</item>
    ///   <item><c>Dispatcher.UnhandledException</c> — UI 线程异常,我们把 <c>Handled=true</c> 撑住 UI</item>
    ///   <item><c>TaskScheduler.UnobservedTaskException</c> — fire-and-forget Task 里的异常(GC 才能拿到)</item>
    /// </list>
    /// </summary>
    private void HookGlobalExceptionHandlers(IGlobalExceptionHandler handler, ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                handler.Report(ex, "AppDomain");
            }
        };

        DispatcherUnhandledException += (_, e) =>
        {
            handler.Report(e.Exception, "Dispatcher");
            // UI 异常不让它把整个 WPF 拉垮 — 让操作员通过红条看到、决定下一步。
            // 真要崩,看门狗 + 心跳超时会兜底。
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 必须先 SetObserved,不论后面要不要弹红条,否则 finalizer 线程
            // 仍会把它当成"未观察"再触发一次,在某些 runtime 配置下会终止进程
            e.SetObserved();

            // Sharp7 1.1.84 TCPPing 在 PLC 不可达时泄露的 socket abort 任务:
            // 业务层已经通过 LastError 处理,UI 由 PlcConnected 徽章反映,这里
            // 不应再弹"系统异常"红条,否则一旦 PLC 离线就是无意义的告警噪音。
            // 把它降级到 Debug 日志,便于排查时仍可看见。
            if (BenignTaskExceptionFilter.IsBenignSocketAbort(e.Exception))
            {
                logger.LogDebug(
                    e.Exception,
                    "已忽略良性的未观察 socket abort Task(常见来源:Sharp7 PLC 重连)");
                return;
            }

            handler.Report(e.Exception, "Task");
        };
    }

    /// <summary>
    /// 优雅退出(由主窗口在退出密码验证通过后调用)。
    /// <para>
    /// 在 UI 线程上<b>异步</b>等待所有后台服务停止:等待期间 UI 线程仍在处理消息,
    /// 需要回到 UI 线程收尾的服务(如 UI 心跳)不会与之互相等待而死锁。全部停止后再关闭应用。
    /// </para>
    /// </summary>
    public async Task ShutdownGracefullyAsync(int exitCode = 0)
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        await StopHostAsync(GracefulStopTimeout);
        Shutdown(exitCode);
    }

    /// <summary>
    /// 应用退出的最后一步:flush 日志并结束进程。
    /// <para>
    /// 正常情况下后台服务已在 <see cref="ShutdownGracefullyAsync"/> 中停止;
    /// 若是其它途径触发的退出(例如 Windows 注销 / 关机),这里在线程池上停止后台服务并限时等待,
    /// 不在 UI 线程上做任何同步等待,避免死锁。
    /// </para>
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var stopTask = Task.Run(() => StopHostAsync(FallbackStopTimeout));
            stopTask.Wait(FallbackStopTimeout + TimeSpan.FromSeconds(2));
        }

        base.OnExit(e);
        Log.CloseAndFlush();

        // 兜底:第三方库(如 Sharp7 的 socket 后台任务)可能残留前台资源而推迟进程退出,
        // 显式 Environment.Exit 保证退出后任务管理器里没有残留进程。
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>停止并释放所有后台服务。可在 UI 线程上 await,也可在线程池上运行;只会执行一次。</summary>
    private async Task StopHostAsync(TimeSpan timeout)
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null) return;

        var logger = host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("ProductionDashboard 退出 · 正在停止后台服务");

        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                await host.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "停止后台服务时出错(继续退出)");
            }
        }

        logger.LogInformation("ProductionDashboard 后台服务已停止");

        try
        {
            // 部分服务只实现了 IAsyncDisposable,必须异步释放
            if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放后台服务时出错(继续退出)");
        }
    }

    /// <summary>
    /// 注册全部服务 —— 整个程序的「接线图」,按层标注来源。
    /// <para>
    /// <b>顺序很重要</b>:后台服务(IHostedService)按注册顺序启动。例如数据库迁移必须最先完成,
    /// 计数服务必须在班次与班组服务之后启动。新增服务时,请按依赖关系插入到合适的位置。
    /// </para>
    /// </summary>
    private static void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddProductionDashboardOptions(configuration);   // [App]   配置绑定 + 启动期校验
        services.AddProductionDashboardClock();                   // [Core]  系统时钟 TimeProvider
        services.AddProductionDashboardDataAccess();              // [Infra] SQLite + Repository + 数据库迁移(最先启动)
        services.AddProductionDashboardPlc();                     // [Infra] PLC 通信(Edge / Handshake)
        services.AddProductionDashboardShifts();                  // [Core]  班次定义加载 + 班次切换检测
        services.AddProductionDashboardAuditLog();                // [Infra] 审计日志写库
        services.AddProductionDashboardAudit();                   // [Core]  审计订阅(班次切换 / PLC 状态)
        services.AddProductionDashboardTeamAssignment();          // [Core]  班组与槽位分配(计数之前加载)
        services.AddProductionDashboardAuth();                    // [Core]  班长登录 + 空闲超时
        services.AddProductionDashboardCounting();                // [Core]  PLC 信号 → 按班组计数 + 异步落库
        services.AddProductionDashboardExcelExport();             // [Infra] Excel 导出实现
        services.AddProductionDashboardAggregation();             // [Core]  分钟聚合 + 班次汇总 + 班次结算
        services.AddProductionDashboardStatistics();              // [Core]  大屏统计(各维度产量 / 目标 / 趋势)
        services.AddProductionDashboardShiftConfiguration();      // [Core]  班组配置保存
        services.AddProductionDashboardMaintenance();             // [Infra] 每日 VACUUM + 备份
        services.AddProductionDashboardYearlyArchive();           // [Infra] 跨年归档
        services.AddProductionDashboardToast();                   // [App]   Toast 通知(全局异常处理之前)
        services.AddProductionDashboardGlobalExceptionHandler();  // [Core]  全局异常汇集
        services.AddProductionDashboardSystemHealth();            // [Infra] 时间 / 磁盘巡检
        services.AddProductionDashboardWatchdog();                // [App]   UI 心跳 + 看门狗子进程
        services.AddProductionDashboardTheme();                   // [App]   深 / 浅主题
        services.AddProductionDashboardViews();                   // [App]   ViewModel + 视图 + 主窗口
    }
}
