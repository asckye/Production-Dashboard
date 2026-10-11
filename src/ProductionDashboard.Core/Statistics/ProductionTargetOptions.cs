using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Core.Statistics;

/// <summary>
/// 周 / 月 / 年目标的换算规则。绑定 appsettings.json 的 <c>Targets</c> 节点(可省略,使用默认值)。
/// <para>
/// 日目标 = 所有班次目标产量之和;周目标 = 日目标 × <see cref="WorkingDaysPerWeek"/>;
/// 月目标 = 日目标 × <see cref="WorkingDaysPerMonth"/>;年目标 = 月目标 × 12。
/// </para>
/// </summary>
public sealed class ProductionTargetOptions
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Targets" 段。</summary>
    public const string SectionName = "Targets";

    /// <summary>每周按多少个生产日计算周目标。默认 5;7×24 连续生产的产线可设为 7。</summary>
    [Range(1, 7)]
    public int WorkingDaysPerWeek { get; init; } = 5;

    /// <summary>每月按多少个生产日计算月目标。默认 22;连续生产的产线可设为 30。</summary>
    [Range(1, 31)]
    public int WorkingDaysPerMonth { get; init; } = 22;
}
