namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班次定义 (版本化)。一个 (Code) 可以有多条不同时段生效的记录,
/// <see cref="EffectiveTo"/> 为 null 表示当前生效。
/// </summary>
/// <param name="Id">主键。新建未持久化时为 0。</param>
/// <param name="Code">班次代码,例如 "A" "B" "C"。同一时间窗口内唯一。</param>
/// <param name="Name">显示名,例如 "白班"。</param>
/// <param name="StartTime">开始时刻(本地)。DB 存 "HH:mm" TEXT,在 Repository 边界转换为 TimeOnly。</param>
/// <param name="EndTime">结束时刻(本地)。DB 存 "HH:mm" TEXT,在 Repository 边界转换为 TimeOnly。</param>
/// <param name="CrossesMidnight">是否跨 0 点(例如 20:00→08:00 的夜班)。</param>
/// <param name="TargetCount">本班次目标产量(件)。</param>
/// <param name="ColorHex">UI 主题色,#RRGGBB。</param>
/// <param name="DisplayOrder">在大屏 / 配置页上的排序(升序)。</param>
/// <param name="EffectiveFrom">从该日期起生效(含)。</param>
/// <param name="EffectiveTo">截至该日期失效(含);null 表示当前生效。</param>
/// <param name="CreatedAt">行创建时间(UTC)。</param>
public sealed record ShiftDefinition(
    int Id,
    string Code,
    string Name,
    TimeOnly StartTime,
    TimeOnly EndTime,
    bool CrossesMidnight,
    int TargetCount,
    string ColorHex,
    int DisplayOrder,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTime CreatedAt)
{
    /// <summary>
    /// 班次时长。跨日班次(如 20:00 → 08:00)也能正确计算;开始与结束相同表示全天 24 小时。
    /// </summary>
    public TimeSpan Duration => StartTime == EndTime ? TimeSpan.FromHours(24) : EndTime - StartTime;
}
