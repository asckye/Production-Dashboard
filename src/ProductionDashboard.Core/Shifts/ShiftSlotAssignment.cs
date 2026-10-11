namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班次槽位的当前班组分配。一个槽位(ShiftDefId)在某个时刻只能被一个班组占据。
/// 倒班 = 更新这张表里两个槽位的 TeamId。
/// </summary>
/// <param name="ShiftDefId">槽位主键(对应 <see cref="ShiftDefinition.Id"/>)。表 PK。</param>
/// <param name="TeamId">当前在该槽位的班组主键(对应 <see cref="Team.Id"/>)。</param>
/// <param name="AssignedAt">本次分配生效的时刻(UTC)。倒班审计与排序用。</param>
/// <param name="AssignedByOperator">本次分配的操作员姓名(LoginDialog 收集);
/// 启动迁移时种子分配填 "migration"。</param>
public sealed record ShiftSlotAssignment(
    int ShiftDefId,
    int TeamId,
    DateTime AssignedAt,
    string? AssignedByOperator);
