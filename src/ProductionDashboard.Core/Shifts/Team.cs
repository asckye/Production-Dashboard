namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班组定义。和 <see cref="ShiftDefinition"/> 的关系是:
/// <list type="bullet">
///   <item>ShiftDefinition = 时间槽位(白班 08:00-20:00 / 夜班 20:00-08:00)</item>
///   <item>Team = 实际跑这条产线的人(A 班 / B 班),持有生产数据</item>
/// </list>
/// 班组通过 <see cref="ShiftSlotAssignment"/> 与槽位关联,倒班=改 ShiftSlotAssignment。
/// </summary>
/// <param name="Id">主键。新建未持久化时为 0。</param>
/// <param name="Name">班组名称,例如 "A 班"。</param>
/// <param name="LeaderName">班长姓名,显示在大屏顶栏。</param>
/// <param name="Members">成员名单(可选),JSON 序列化的字符串数组。null 表示不维护成员列表。</param>
/// <param name="ColorHex">UI 主题色,#RRGGBB。和 ShiftDefinition 的颜色独立,以便倒班后槽位颜色不变、班组颜色跟人走。</param>
/// <param name="DisplayOrder">排序(升序)。</param>
/// <param name="IsActive">是否激活。软删除标志,false 时不出现在选择列表里但历史数据仍可关联。</param>
/// <param name="CreatedAt">创建时间(UTC)。</param>
public sealed record Team(
    int Id,
    string Name,
    string LeaderName,
    string? Members,
    string ColorHex,
    int DisplayOrder,
    bool IsActive,
    DateTime CreatedAt);
