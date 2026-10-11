namespace ProductionDashboard.Core.Shifts;

/// <summary>跨日班次归属规则。</summary>
public enum ShiftAttributionRule
{
    /// <summary>归属到班次开始的那一天 (推荐,例如 20:00 开始的夜班算作开始日)。</summary>
    StartDate = 0,

    /// <summary>归属到班次结束的那一天。</summary>
    EndDate = 1,
}
