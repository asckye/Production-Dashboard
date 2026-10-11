namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 保存「班组配置」页面的修改:班次定义与班组信息。
/// 保存后会通知运行中的服务(<see cref="IShiftManager"/> / <see cref="ITeamAssignmentService"/>)重新加载,
/// 所以修改立即生效。界面层只调用这个服务,不直接访问数据仓库。
/// </summary>
public interface IShiftConfigurationService
{
    /// <summary>
    /// 在一个事务里保存班次定义的增、删、改以及班次的班组分配,然后重新加载班次和班组分配。
    /// 失败时数据库不变,可以直接重试。
    /// </summary>
    Task SaveShiftDefinitionsAsync(ShiftDefinitionChanges changes, CancellationToken cancellationToken);

    /// <summary>保存班组名称 / 班长的修改,然后重新加载班组分配。</summary>
    Task SaveTeamsAsync(IReadOnlyList<Team> teams, CancellationToken cancellationToken);
}

/// <summary>一次保存涉及的班次定义变更。</summary>
/// <param name="DeletedIds">要删除的班次 Id(软删除:保存后立即不再生效,历史数据保留;它的班组分配一并删除)。</param>
/// <param name="Added">新增的班次(Id 为 0)。</param>
/// <param name="Updated">修改过的已有班次。</param>
/// <param name="TeamByCode">
/// 要设置的班组分配:班次代号 → 班组 Id,只需包含新增的和改了班组的班次。
/// 用代号而不是 Id,是因为新增班次在保存前还没有 Id(保存后仍在生效的班次之间代号不重复)。
/// 没有班组的班次,产量无法记录,界面保存前会要求选择。
/// </param>
/// <param name="ChangedBy">操作员,写入分配记录和审计。</param>
public sealed record ShiftDefinitionChanges(
    IReadOnlyList<int> DeletedIds,
    IReadOnlyList<ShiftDefinition> Added,
    IReadOnlyList<ShiftDefinition> Updated,
    IReadOnlyDictionary<string, int>? TeamByCode = null,
    string? ChangedBy = null);
