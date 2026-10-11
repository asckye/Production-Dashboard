using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Production;

/// <summary>
/// 班次结算:班次切换时,为旧班次时段内每个参与过的班组写入最终 <c>ShiftSummary</c>
/// (实际产量 + 峰值小时),并导出该班次 Excel。
/// 从 <see cref="AggregationHostedService"/> 拆出,使这段最复杂的逻辑独立、可单测。
/// </summary>
public interface IShiftSettlementService
{
    /// <summary>
    /// 结算指定班次周期。内部已处理异常(记录日志 + 告警):班次结算每班只触发一次,
    /// 失败会导致该班次最终汇总永久缺失,因此不向上抛,而是留痕 + 弹 UI 告警。
    /// </summary>
    Task SettleAsync(ShiftAssignment shift, CancellationToken cancellationToken);
}
