using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Core.Statistics;

namespace ProductionDashboard.Core.DependencyInjection;

/// <summary>
/// Core 层服务注册 — 班次、班组、计数、聚合、登录、审计订阅、全局异常。
/// <para>
/// 每个方法只注册一个功能。很多服务同时是 <see cref="IHostedService"/>,
/// 而 Host 按注册顺序启动它们,所以<b>调用顺序由 App.xaml.cs 统一决定</b>,不要在这里互相调用。
/// </para>
/// </summary>
public static class CoreServiceRegistration
{
    /// <summary>
    /// 注册系统时钟 <see cref="TimeProvider"/>。业务代码一律通过它读取当前时间(不要直接用 DateTime.Now),
    /// 这样测试里可以换成可控的时钟。
    /// </summary>
    public static IServiceCollection AddProductionDashboardClock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// 注册 <see cref="ShiftManager"/>:同一实例同时充当
    /// <see cref="IShiftManager"/> 和 <see cref="IHostedService"/>,
    /// 启动期加载班次定义并启动周期性切换检测。
    /// </summary>
    public static IServiceCollection AddProductionDashboardShifts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSelfHostedSingleton<ShiftManager, IShiftManager>();
    }

    /// <summary>
    /// 注册审计订阅后台服务:监听班次切换、PLC 连接状态等系统事件并写入审计日志。
    /// 依赖 <see cref="IAuditLogger"/> 的实现(由 Infrastructure 层注册)。
    /// </summary>
    public static IServiceCollection AddProductionDashboardAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<AuditSubscriberHostedService>();
        return services;
    }

    /// <summary>
    /// 注册 <see cref="TeamAssignmentService"/>:同一实例同时充当
    /// <see cref="ITeamAssignmentService"/> 和 <see cref="IHostedService"/>,
    /// 启动期加载班组与当前分配。依赖 <see cref="IAuditLogger"/>。
    /// </summary>
    public static IServiceCollection AddProductionDashboardTeamAssignment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSelfHostedSingleton<TeamAssignmentService, ITeamAssignmentService>();
    }

    /// <summary>
    /// 注册 <see cref="AuthService"/>(班长登录态 + 空闲超时)。依赖 <see cref="IAuditLogger"/>。
    /// </summary>
    public static IServiceCollection AddProductionDashboardAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSelfHostedSingleton<AuthService, IAuthService>();
    }

    /// <summary>
    /// 注册 <see cref="CountingService"/>:订阅 PLC + ShiftManager,
    /// 内存计数 + 异步落库的中枢。必须在 ShiftManager 与 TeamAssignment 之后启动。
    /// </summary>
    public static IServiceCollection AddProductionDashboardCounting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSelfHostedSingleton<CountingService, ICountingService>();
    }

    /// <summary>
    /// 注册聚合相关服务:<see cref="AggregationHostedService"/>(分钟聚合 + 班次汇总刷新 +
    /// 触发结算)、<see cref="IShiftSettlementService"/>(班次结算 + Excel 导出)。
    /// 结算依赖的 Excel 导出实现由 Infrastructure 层注册。
    /// </summary>
    public static IServiceCollection AddProductionDashboardAggregation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMinuteAggregator, MinuteAggregator>();
        services.AddSingleton<IShiftSummaryRefresher, ShiftSummaryRefresher>();
        services.AddSingleton<IShiftSettlementService, ShiftSettlementService>();
        services.AddHostedService<AggregationHostedService>();
        return services;
    }

    /// <summary>注册大屏统计服务 <see cref="IProductionStatisticsService"/>(各维度产量、目标与趋势)。</summary>
    public static IServiceCollection AddProductionDashboardStatistics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IProductionStatisticsService, ProductionStatisticsService>();
        return services;
    }

    /// <summary>注册班组配置保存服务 <see cref="IShiftConfigurationService"/>。</summary>
    public static IServiceCollection AddProductionDashboardShiftConfiguration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IShiftConfigurationService, ShiftConfigurationService>();
        return services;
    }

    /// <summary>注册全局异常汇集点 <see cref="IGlobalExceptionHandler"/>。</summary>
    public static IServiceCollection AddProductionDashboardGlobalExceptionHandler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IGlobalExceptionHandler, GlobalExceptionHandler>();
        return services;
    }
}
