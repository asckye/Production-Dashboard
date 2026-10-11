using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Core.Statistics;
using ProductionDashboard.Infrastructure.Maintenance;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Configuration;

/// <summary>
/// 配置(Options 模式)注册 — 把 appsettings.json 的各个节点绑定到强类型配置类。
/// 全部启用 DataAnnotations 校验 + 启动期校验:配置缺失或不合法时程序启动即报错,而不是运行中才出问题。
/// <para>新增配置类时,在这里加一行 <c>AddValidatedOptions</c> 即可。</para>
/// </summary>
public static class OptionsRegistration
{
    /// <summary>注册所有配置节点。</summary>
    public static IServiceCollection AddProductionDashboardOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Core
        services.AddValidatedOptions<ShiftOptions>(configuration, ShiftOptions.SectionName);
        services.AddValidatedOptions<AuthOptions>(configuration, AuthOptions.SectionName);
        services.AddValidatedOptions<ProductionTargetOptions>(configuration, ProductionTargetOptions.SectionName);

        // Infrastructure
        services.AddValidatedOptions<PlcOptions>(configuration, PlcOptions.SectionName);
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<SystemHealthOptions>(configuration, SystemHealthOptions.SectionName);

        // App
        services.AddValidatedOptions<DisplayOptions>(configuration, DisplayOptions.SectionName);
        services.AddValidatedOptions<WatchdogOptions>(configuration, WatchdogOptions.SectionName);

        return services;
    }

    private static void AddValidatedOptions<TOptions>(
        this IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
