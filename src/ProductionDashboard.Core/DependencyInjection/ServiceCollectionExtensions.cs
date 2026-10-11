using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ProductionDashboard.Core.DependencyInjection;

/// <summary>DI 注册的通用辅助。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// "三角注册":同一个单例实例同时以三种身份解析 —— 具体类型 <typeparamref name="TConcrete"/>、
    /// 服务接口 <typeparamref name="TInterface"/>、以及 <see cref="IHostedService"/>。
    /// 保证拿到的永远是同一个实例(避免状态分裂),同时让 Host 正确管理其启动/停止生命周期。
    ///
    /// <para>注意:不能用 <c>AddHostedService&lt;T&gt;</c>,
    /// 那会新建一个独立实例,与作为 <typeparamref name="TInterface"/> 解析到的不是同一个。</para>
    /// </summary>
    public static IServiceCollection AddSelfHostedSingleton<TConcrete, TInterface>(
        this IServiceCollection services)
        where TConcrete : class, TInterface, IHostedService
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TConcrete>();
        services.AddSingleton<TInterface>(sp => sp.GetRequiredService<TConcrete>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TConcrete>());
        return services;
    }
}
