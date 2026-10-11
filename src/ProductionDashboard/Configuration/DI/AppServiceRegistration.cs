using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProductionDashboard.Services.UI;
using ProductionDashboard.ViewModels;
using ProductionDashboard.Views;
using ProductionDashboard.Watchdog;

namespace ProductionDashboard.Configuration;

/// <summary>
/// App 层(WPF 桌面程序)服务注册 — 主题、Toast 通知、UI 心跳与看门狗、视图与 ViewModel。
/// 与 Core / Infrastructure 的注册方法一样,调用顺序由 App.xaml.cs 统一决定。
/// </summary>
public static class AppServiceRegistration
{
    /// <summary>
    /// 注册 Toast 通知服务。具体实例同时以 <see cref="IToastService"/> 和
    /// <see cref="ToastService"/> 暴露:后者供 <c>ToastHost</c> 直接绑定通知列表。
    /// </summary>
    public static IServiceCollection AddProductionDashboardToast(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
        return services;
    }

    /// <summary>
    /// 注册 UI 心跳 + 看门狗子进程。<c>Watchdog:Enabled=false</c> 时两者都不会真正起作用。
    /// <see cref="UiHeartbeatHostedService"/> 同时作为单例和后台服务注册,
    /// 因为 <see cref="WatchdogLauncherHostedService"/> 需要读取它的心跳文件路径。
    /// </summary>
    public static IServiceCollection AddProductionDashboardWatchdog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<UiHeartbeatHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UiHeartbeatHostedService>());
        services.AddSingleton<IHostedService, WatchdogLauncherHostedService>();
        return services;
    }

    /// <summary>注册主题切换服务。启动期会读 Data/theme.json 恢复上次选择的主题。</summary>
    public static IServiceCollection AddProductionDashboardTheme(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IThemeService, ThemeService>();
        return services;
    }

    /// <summary>
    /// 注册 ViewModel、视图、对话框服务与主窗口。大屏只有一个窗口实例,全部注册为单例。
    /// </summary>
    public static IServiceCollection AddProductionDashboardViews(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ShiftConfigViewModel>();

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<DashboardView>();
        services.AddSingleton<ShiftConfigView>();
        services.AddSingleton<ToastHost>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
