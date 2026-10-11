using ProductionDashboard.Core.Production;

namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班组与槽位分配的运行时状态管理。包装 <see cref="ITeamRepository"/>,
/// 持有内存缓存供 CountingService / Dashboard 高频读取,并广播变更事件。
/// </summary>
public interface ITeamAssignmentService
{
    /// <summary>当前所有激活的班组,按 DisplayOrder 升序。</summary>
    IReadOnlyList<Team> Teams { get; }

    /// <summary>当前所有槽位的分配快照。Key = ShiftDefId。</summary>
    IReadOnlyDictionary<int, Team> CurrentAssignments { get; }

    /// <summary>查指定槽位当前的班组,无分配或槽位不存在返回 null。</summary>
    Team? GetTeamForSlot(int shiftDefId);

    /// <summary>从 DB 重新加载 Teams 与 CurrentAssignments。</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 2 槽位对调(倒班切换):交换两个班次槽位上的班组归属。
    /// 当前槽位数 != 2 时抛 <see cref="InvalidOperationException"/>。
    /// 数据库 / 内存 / AuditLog 三处一致,失败回滚。
    /// </summary>
    /// <param name="operatorName">执行倒班的操作员姓名(登录态 IAuthService.CurrentOperator)。</param>
    Task SwapTwoSlotsAsync(string operatorName, CancellationToken cancellationToken);

    /// <summary>
    /// 把指定槽位重新分配给指定班组(N 槽位 / N 班组通用入口)。
    /// </summary>
    Task ReassignAsync(
        int shiftDefId, int teamId, string operatorName, CancellationToken cancellationToken);

    /// <summary>
    /// 分配变更(swap / reassign / team CRUD 完成后)。
    /// 订阅方:CountingService 切换写入的 TeamId、Dashboard 刷新班组显示。
    /// </summary>
    event Action? AssignmentsChanged;
}
