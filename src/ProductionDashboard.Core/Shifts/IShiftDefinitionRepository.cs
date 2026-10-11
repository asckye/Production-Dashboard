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

    /// <summary>插入新班次,返回新生成的 Id。</summary>
    Task<int> InsertShiftDefinitionAsync(
        ShiftDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// 在<b>同一个事务</b>里保存一次配置修改:软删除、新增、修改(按 Id,Code 不变以免破坏外键关联)。
    /// 任何一步失败都整体回滚,不会留下"保存了一半"的状态,重试也不会重复新增。
    /// <para>
    /// 软删除不删行,只把 EffectiveTo 设为 <paramref name="deletedLastEffectiveDay"/>(最后生效的一天),
    /// 历史 ShiftSummary / ProductionEvent 的外键仍然有效。
    /// </para>
    /// </summary>
    Task SaveChangesAsync(
        ShiftDefinitionChanges changes, DateOnly deletedLastEffectiveDay, CancellationToken cancellationToken);
}
