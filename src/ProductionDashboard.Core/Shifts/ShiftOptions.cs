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

    /// <summary>
    /// 班次切换检测周期 (ms),决定界面多快切换到新班次(清零 Hero 大数字、高亮新班次卡片)。
    /// 产量本身按信号的时间戳归属班次,不受这个周期影响。
    /// </summary>
    [Range(1000, 600_000)]
    public int CheckIntervalMs { get; init; } = 1000;
}
