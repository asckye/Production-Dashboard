using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班次行为配置。绑定 <c>Shift</c> 节点。
/// 班次定义本身存在数据库的 ShiftDefinition 表里,这里只放运行时策略。
/// </summary>
public sealed class ShiftOptions
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Shift" 段。</summary>
    public const string SectionName = "Shift";

    /// <summary>
    /// 跨 0 点班次的归属规则。
    /// 默认 <see cref="ShiftAttributionRule.EndDate"/>:夜班 5/20 20:00 → 5/21 08:00 算到 5/21,
    /// 跟现场"今天上的是哪一班"的口语一致(夜班"上的是第二天的班")。
    /// </summary>
    public ShiftAttributionRule AttributionRule { get; init; } = ShiftAttributionRule.EndDate;

    /// <summary>班次切换检测周期 (ms)。30 秒 = 边界感知最多滞后 30 秒,足够生产统计。</summary>
    [Range(1000, 600_000)]
    public int CheckIntervalMs { get; init; } = 30_000;
}
