namespace ProductionDashboard.ViewModels.Dashboard;

/// <summary>
/// 5 维度卡的「比上小时/昨日/上周/上月/去年 ±X%」趋势文本格式化。
/// 纯函数,无外部依赖,可大量单测覆盖边界。
/// </summary>
public static class TrendFormatter
{
    /// <summary>
    /// 把当前值与上期值格式化成趋势短语。
    /// <list type="bullet">
    ///   <item>previous == 0 且 current == 0 → "—"(无对比基准)</item>
    ///   <item>previous == 0 且 current &gt; 0 → "比&lt;previousLabel&gt; 新增"</item>
    ///   <item>否则 → "↑ 比&lt;previousLabel&gt; +X.X%" 或 "↓ ... -X.X%"</item>
    /// </list>
    /// </summary>
    public static string FormatTrend(int current, int previous, string previousLabel)
    {
        if (previous == 0)
        {
            return current == 0 ? "—" : $"比{previousLabel} 新增";
        }
        var delta = (current - previous) * 100.0 / previous;
        var arrow = delta >= 0 ? "↑" : "↓";
        return $"{arrow} 比{previousLabel} {delta:+0.0;-0.0;0}%";
    }
}
