namespace ProductionDashboard.Core.Production;

/// <summary>
/// PLC 上报的一次"完成"信号 (一件产品)。
/// <para>
/// 数据归属是双层的:
/// <list type="bullet">
///   <item><see cref="ShiftDefId"/> 标记发生在哪个时间槽位(白班 / 夜班)</item>
///   <item><see cref="TeamId"/> 标记由哪个班组生产 — 真正的累计统计主体,跨倒班保留</item>
/// </list>
/// </para>
/// </summary>
/// <param name="Id">数据库主键。新事件未持久化时为 0。</param>
/// <param name="Timestamp">事件发生的本地时间(信号上升沿到达的瞬间)。</param>
/// <param name="ShiftDate">该事件归属的班次日期(跨日按归属规则决定)。</param>
/// <param name="ShiftDefId">归属班次定义的主键(时间槽位)。</param>
/// <param name="TeamId">归属班组主键(产量跟班组走,倒班后仍累计到原班组)。</param>
/// <param name="Source">事件来源标记。默认 "PLC",历史补录或测试可以是其他值。</param>
public sealed record ProductionEvent(
    long Id,
    DateTime Timestamp,
    DateOnly ShiftDate,
    int ShiftDefId,
    int TeamId,
    string Source = "PLC");
