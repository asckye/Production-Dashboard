using System.Reflection;
using FluentAssertions;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.ViewModels;

namespace ProductionDashboard.Tests.Architecture;

/// <summary>
/// 分层规则守护测试。违反规则时测试失败,并在消息里说明该怎么改。
/// 规则说明见 docs/ARCHITECTURE.md。
/// </summary>
public class LayeringTests
{
    private static readonly Assembly CoreAssembly = typeof(ShiftCalculator).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(SqliteConnectionFactory).Assembly;
    private static readonly Assembly AppAssembly = typeof(MainViewModel).Assembly;

    private static readonly string[] WpfAssemblies = ["PresentationFramework", "PresentationCore", "WindowsBase"];
    private static readonly string[] TechnologyAssemblies = ["Microsoft.Data.Sqlite", "Dapper", "Sharp7", "ClosedXML"];

    [Fact]
    public void Core_does_not_depend_on_other_layers_or_technologies()
    {
        var forbidden = WpfAssemblies
            .Concat(TechnologyAssemblies)
            .Append(InfrastructureAssembly.GetName().Name!)
            .Append(AppAssembly.GetName().Name!);

        ReferencedAssemblyNames(CoreAssembly).Should().NotContain(forbidden,
            "Core 只放业务规则与接口;数据库、PLC、Excel 的实现放 Infrastructure,界面放 App");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_app_or_wpf()
    {
        var forbidden = WpfAssemblies.Append(AppAssembly.GetName().Name!);

        ReferencedAssemblyNames(InfrastructureAssembly).Should().NotContain(forbidden,
            "Infrastructure 只实现 Core 的接口,不能反过来依赖界面层");
    }

    [Fact]
    public void ViewModels_use_core_services_instead_of_repositories_or_infrastructure()
    {
        var violations =
            from type in AppAssembly.GetTypes()
            where type.Namespace?.StartsWith("ProductionDashboard.ViewModels", StringComparison.Ordinal) == true
            from dependency in DependencyTypes(type)
            where dependency.Assembly == InfrastructureAssembly || IsRepositoryInterface(dependency)
            select $"{type.Name} → {dependency.Name}";

        violations.Should().BeEmpty(
            "ViewModel 应调用 Core 服务(例如 IProductionStatisticsService / IShiftConfigurationService),不直接访问数据仓库或基础设施");
    }

    private static IEnumerable<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static bool IsRepositoryInterface(Type type) =>
        type.IsInterface && type.Name.StartsWith('I') && type.Name.EndsWith("Repository", StringComparison.Ordinal);

    /// <summary>类型通过构造函数参数、字段和属性依赖的其它类型。</summary>
    private static IEnumerable<Type> DependencyTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var constructorParameters = type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
        var fields = type.GetFields(all).Select(f => f.FieldType);
        var properties = type.GetProperties(all).Select(p => p.PropertyType);

        return constructorParameters.Concat(fields).Concat(properties).Distinct();
    }
}
