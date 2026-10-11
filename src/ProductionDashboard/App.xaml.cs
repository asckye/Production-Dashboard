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
    private IHost? _host;

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

    /// <summary>应用退出 — 优雅停 host(2s + 3s 兜底),flush 日志,Environment.Exit 兜底进程退出。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("ProductionDashboard 退出");

            try
            {
                // 2s 超时给所有 hosted service 收尾(看门狗 kill / 心跳标记 / DB Channel drain)
                // 外层再加 3s 硬超时:任何一个忽略 cancellationToken 的服务(例如卡在
                // 同步阻塞 IO 的 PLC 库)不会再把 UI 线程拖死,过期就抛弃等待继续走。
                //
                // 用 Task.Run 把 StopAsync 派到线程池:若某个 StopAsync 内部回到 UI 线程
                // (Dispatcher.Invoke)而此刻 UI 线程正阻塞在下面的 Wait,就会经典死锁。
                // 放到线程池线程跑可避免这个 sync-over-async 陷阱。
                var stopTask = Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(2)));
                if (!stopTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    logger.LogWarning("Host.StopAsync 超过 3s 未完成,放弃等待并强制退出");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Host.StopAsync 抛异常,继续走 Dispose");
            }

            try { _host.Dispose(); }
            catch (Exception ex) { logger.LogWarning(ex, "Host.Dispose 失败"); }
            _host = null;
        }

        // 先让 base.OnExit 跑完 — 它会触发 Application.Exit 事件 + 关掉剩余窗口,
        // 期间订阅方可能还在写日志;之后再 CloseAndFlush,把这些尾巴日志也冲到磁盘。
        base.OnExit(e);
        Log.CloseAndFlush();

        // 兜底:某些第三方库(如 Sharp7 内部的 socket 后台任务)在 PLC 不可达时会
        // 残留尚未结束的非托管资源 / 终结器任务,偶发性地延后进程退出。
        // 显式 Environment.Exit 保证用户点退出后任务管理器里 0 残留。
        Environment.Exit(e.ApplicationExitCode);
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
