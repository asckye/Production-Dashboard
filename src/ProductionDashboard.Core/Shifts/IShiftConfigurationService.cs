namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 保存「班组配置」页面的修改:班次定义与班组信息。
/// 保存后会通知运行中的服务(<see cref="IShiftManager"/> / <see cref="ITeamAssignmentService"/>)重新加载,
/// 所以修改立即生效。界面层只调用这个服务,不直接访问数据仓库。
/// </summary>
public interface IShiftConfigurationService
{
    /// <summary>在一个事务里保存班次定义的增、删、改,然后重新加载班次。失败时数据库不变,可以直接重试。</summary>
    Task SaveShiftDefinitionsAsync(ShiftDefinitionChanges changes, CancellationToken cancellationToken);

    /// <summary>保存班组名称 / 班长的修改,然后重新加载班组分配。</summary>
    Task SaveTeamsAsync(IReadOnlyList<Team> teams, CancellationToken cancellationToken);
}

/// <summary>一次保存涉及的班次定义变更。</summary>
/// <param name="DeletedIds">要删除的班次 Id(软删除:保存后立即不再生效,历史数据保留)。</param>
/// <param name="Added">新增的班次(Id 为 0)。</param>
/// <param name="Updated">修改过的已有班次。</param>
public sealed record ShiftDefinitionChanges(
    IReadOnlyList<int> DeletedIds,
    IReadOnlyList<ShiftDefinition> Added,
    IReadOnlyList<ShiftDefinition> Updated);
