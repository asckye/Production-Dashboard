using ProductionDashboard.Core.Production;

namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班组与槽位分配的持久化访问。班组是与生产事件链路(<see cref="IProductionEventRepository"/>)
/// 解耦的核心实体,接口面较窄,独立维护更清晰。
/// UI 层(班组管理 / 倒班切换)只需要 ITeamRepository,不必依赖任何生产事件接口。
/// </summary>
public interface ITeamRepository
{
    // ── Team CRUD ────────────────────────────────────────────────────

    /// <summary>列出所有 <c>IsActive=true</c> 的班组,按 DisplayOrder 升序。</summary>
    Task<IReadOnlyList<Team>> GetActiveTeamsAsync(CancellationToken cancellationToken);

    /// <summary>读取所有班组(含未激活),用于管理页历史回看。</summary>
    Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken);

    /// <summary>查单个班组,不存在返回 null。</summary>
    Task<Team?> GetTeamAsync(int id, CancellationToken cancellationToken);

    /// <summary>插入新班组,返回新 Id。</summary>
    Task<int> InsertTeamAsync(Team team, CancellationToken cancellationToken);

    /// <summary>按 Id 更新班组(Name / LeaderName / Members / ColorHex / DisplayOrder / IsActive)。</summary>
    Task UpdateTeamAsync(Team team, CancellationToken cancellationToken);

    /// <summary>软删除:把 IsActive 置 false。历史数据中的 TeamId 仍可关联。</summary>
    Task DeactivateTeamAsync(int id, CancellationToken cancellationToken);

    // ── ShiftSlotAssignment ──────────────────────────────────────────

    /// <summary>列出当前所有槽位的分配,按 ShiftDefId 升序。</summary>
    Task<IReadOnlyList<ShiftSlotAssignment>> GetCurrentAssignmentsAsync(
        CancellationToken cancellationToken);

    /// <summary>查单个槽位当前分配的班组,不存在返回 null。</summary>
    Task<ShiftSlotAssignment?> GetAssignmentAsync(
        int shiftDefId, CancellationToken cancellationToken);

    /// <summary>
    /// 插入或更新单个槽位的班组分配(PK = ShiftDefId)。
    /// </summary>
    Task UpsertAssignmentAsync(
        ShiftSlotAssignment assignment, CancellationToken cancellationToken);

    /// <summary>
    /// 原子地更新多个槽位的分配(用于倒班对调:两条 UPSERT 在同一事务)。
    /// </summary>
    Task UpsertAssignmentsAtomicAsync(
        IReadOnlyList<ShiftSlotAssignment> assignments, CancellationToken cancellationToken);
}
