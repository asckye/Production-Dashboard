
namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// "某一时刻属于哪个班次" 的判定结果。
/// <para>
/// <see cref="ShiftDate"/> 是这次班次归属的日期(按归属规则,可能是开始日或结束日);
/// <see cref="PeriodStart"/>/<see cref="PeriodEnd"/> 是该班次此次实际跨越的本地时段。
/// </para>
/// </summary>
/// <param name="Definition">命中的班次定义(包含 Code / Name / Target 等)。</param>
/// <param name="ShiftDate">该班次归属的日期。跨日班次依 <c>ShiftAttributionRule</c> 决定算开始日还是结束日。</param>
/// <param name="PeriodStart">该班次本次实际开始的本地时间(含)。</param>
/// <param name="PeriodEnd">该班次本次实际结束的本地时间(不含)。跨日时 = 次日同时刻。</param>
public sealed record ShiftAssignment(
    ShiftDefinition Definition,
    DateOnly ShiftDate,
    DateTime PeriodStart,
    DateTime PeriodEnd);
