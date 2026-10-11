using ProductionDashboard.Core.Production;

namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班次定义(<see cref="ShiftDefinition"/>)的 CRUD 边界。
/// 现场配置页 / 启动期班次集加载会消费这个接口。
/// </summary>
public interface IShiftDefinitionRepository
{
    /// <summary>列出在 <paramref name="asOf"/> 当天生效的所有班次。</summary>
    Task<IReadOnlyList<ShiftDefinition>> GetActiveShiftDefinitionsAsync(
        DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>查询单个班次定义。</summary>
    Task<ShiftDefinition?> GetShiftDefinitionAsync(
        int id, CancellationToken cancellationToken);

    /// <summary>
    /// 更新班次定义 (按 Id)。Code 不允许改动避免破坏外键关联。
    /// 现场配置界面用这个写回 SQLite。
    /// </summary>
    Task UpdateShiftDefinitionAsync(
        ShiftDefinition definition, CancellationToken cancellationToken);

    /// <summary>插入新班次,返回新生成的 Id。</summary>
    Task<int> InsertShiftDefinitionAsync(
        ShiftDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// 软删除班次:把 EffectiveTo 设为 <paramref name="effectiveTo"/>。
    /// 不真删行,保留历史 ShiftSummary / ProductionEvent 的外键有效。
    /// </summary>
    Task SoftDeleteShiftDefinitionAsync(
        int id, DateOnly effectiveTo, CancellationToken cancellationToken);
}
