using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Export;
using ProductionDashboard.Core.Plc;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Export;
using ProductionDashboard.Infrastructure.Maintenance;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure 层服务注册 — 把 Core 接口绑定到具体实现(SQLite、Sharp7、ClosedXML),
/// 并注册数据库维护类后台任务。
/// <para>
/// 与 <c>CoreServiceRegistration</c> 一样,每个方法只注册一个功能,
/// <b>调用顺序由 App.xaml.cs 统一决定</b>(后台服务按注册顺序启动)。
/// </para>
/// </summary>
public static class InfrastructureServiceRegistration
{
    /// <summary>
    /// 注册数据访问:SQLite 连接工厂、各 Repository(按职责拆分,调用方只依赖用到的小接口),
    /// 以及启动期执行数据库迁移的 <see cref="DatabaseInitializer"/>。必须最先注册。
    /// </summary>
    public static IServiceCollection AddProductionDashboardDataAccess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IShiftDefinitionRepository, ShiftDefinitionRepository>();
        services.AddSingleton<IProductionEventRepository, ProductionEventRepository>();
        services.AddSingleton<IShiftSummaryRepository, ShiftSummaryRepository>();
        services.AddSingleton<IProductionAnalyticsRepository, ProductionAnalyticsRepository>();
        services.AddSingleton<IDataLifecycleRepository, DataLifecycleRepository>();
        services.AddSingleton<ITeamRepository, TeamRepository>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }

    /// <summary>
    /// 注册 PLC 通信服务。根据 <see cref="PlcOptions.SampleMode"/> 选择
    /// Edge(默认,上升沿 + 去抖)或 Handshake(握手位 + 序号字)实现。
    /// 同一实例既作为 <see cref="IPlcService"/> 也作为 <see cref="IHostedService"/>,
    /// 让 Host 启动期自动连接、退出期自动断开。
    /// </summary>
    public static IServiceCollection AddProductionDashboardPlc(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPlcService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PlcOptions>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return options.Value.SampleMode switch
            {
                PlcSampleMode.Edge =>
                    new Sharp7PlcService(options, loggerFactory.CreateLogger<Sharp7PlcService>()),
                PlcSampleMode.Handshake =>
                    new Sharp7PlcHandshakeService(options, loggerFactory.CreateLogger<Sharp7PlcHandshakeService>()),
                // 显式列出已知模式;未知值直接抛错,而不是静默降级到 Edge(否则配错难诊断)。
                var unknown => throw new InvalidOperationException(
                    $"不支持的 PlcSampleMode '{unknown}',请检查 appsettings.json 的 Plc:SampleMode(仅 Edge / Handshake)。"),
            };
        });

        // 让 Host 也把 IPlcService 当成 hosted service 启动/停止。
        // 用 AddSingleton<IHostedService> 而不是 AddHostedService<T>,
        // 是因为后者的泛型推断会把 T 推成 IHostedService 接口本身、注册冲突。
        services.AddSingleton<IHostedService>(sp =>
            (PlcServiceBase)sp.GetRequiredService<IPlcService>());

        return services;
    }

    /// <summary>注册审计日志的数据库实现 <see cref="IAuditLogger"/>(写入 AuditLog 表)。</summary>
    public static IServiceCollection AddProductionDashboardAuditLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAuditLogger, AuditLogger>();
        return services;
    }

    /// <summary>注册 Excel 导出实现(ClosedXML):班次结束导出与年度归档都用它。</summary>
    public static IServiceCollection AddProductionDashboardExcelExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IExcelExportService, ClosedXmlExportService>();
        return services;
    }

    /// <summary>注册数据库维护后台任务(每日 VACUUM + 备份 + 清理过期备份)。</summary>
    public static IServiceCollection AddProductionDashboardMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<DatabaseMaintenanceHostedService>();
        return services;
    }

    /// <summary>注册年度归档服务:跨年时把上一年数据导出为 Excel,然后从主库清掉。</summary>
    public static IServiceCollection AddProductionDashboardYearlyArchive(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<YearlyArchiveHostedService>();
        return services;
    }

    /// <summary>注册系统时间漂移 / 磁盘空间巡检的后台任务。</summary>
    public static IServiceCollection AddProductionDashboardSystemHealth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<SystemHealthMonitor>();
        return services;
    }
}
