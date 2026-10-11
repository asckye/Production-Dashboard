using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Statistics;

/// <summary>一个统计维度(本小时 / 今日 / 本周 / 本月 / 本年)的产量、上一周期产量与目标。</summary>
/// <param name="Actual">本周期累计产量。</param>
/// <param name="Previous">上一周期的产量(上小时 / 昨日 / 上周 / 上月 / 去年),用于趋势对比。</param>
/// <param name="Target">本周期目标产量;0 表示未设置目标。</param>
public sealed record PeriodStatistics(int Actual, int Previous, int Target)
{
    /// <summary>完成度百分比(0-100,超过目标按 100 计);未设置目标时为 0。</summary>
    public double CompletionPercent => Target <= 0 ? 0 : Math.Min(100.0, Actual * 100.0 / Target);
}

/// <summary>一个班组在本周 / 本月 / 本年的累计产量(数据归属跟班组走,倒班后仍累计到原班组)。</summary>
public sealed record TeamTotals(string TeamName, int Week, int Month, int Year);

/// <summary>
/// 大屏 5 个维度卡 + 班组累计对比 + 趋势小柱图所需的全部数字。
/// 由 <see cref="IProductionStatisticsService.GetDashboardStatisticsAsync"/> 一次性计算好,界面层只负责展示。
/// </summary>
/// <param name="Periods">本次统计使用的时间边界。</param>
/// <param name="Hour">本小时。</param>
/// <param name="Day">今日(按班次归属日期)。</param>
/// <param name="Week">本周。</param>
/// <param name="Month">本月。</param>
/// <param name="Year">本年。</param>
/// <param name="TeamA">第一个班组(按显示顺序)的累计;没有班组时为 null。</param>
/// <param name="TeamB">第二个班组的累计;不足两个班组时为 null。</param>
/// <param name="TodayHourly">今天 0-23 时每小时产量。</param>
/// <param name="YesterdayHourly">昨天 0-23 时每小时产量。</param>
/// <param name="LastSevenDays">最近 7 天(含今天)每日产量。</param>
/// <param name="LastSixWeeks">最近 6 周(含本周)每日产量。</param>
/// <param name="LastSixMonths">最近 6 个月(含本月)每日产量。</param>
/// <param name="LastThreeYears">最近 3 年(含本年)每日产量。</param>
public sealed record DashboardStatistics(
    ProductionPeriods Periods,
    PeriodStatistics Hour,
    PeriodStatistics Day,
    PeriodStatistics Week,
    PeriodStatistics Month,
    PeriodStatistics Year,
    TeamTotals? TeamA,
    TeamTotals? TeamB,
    IReadOnlyList<int> TodayHourly,
    IReadOnlyList<int> YesterdayHourly,
    IReadOnlyList<DailyCount> LastSevenDays,
    IReadOnlyList<DailyCount> LastSixWeeks,
    IReadOnlyList<DailyCount> LastSixMonths,
    IReadOnlyList<DailyCount> LastThreeYears);

/// <summary>某个班次「最近一次或正在进行」的周期内的产量。</summary>
/// <param name="Definition">班次定义。</param>
/// <param name="Count">该周期内的产量。班次进行中时统计到当前时刻,结束后保持终值直到下次开始。</param>
public sealed record ShiftPeriodCount(ShiftDefinition Definition, int Count);

/// <summary>白班 / 夜班卡片与当前节拍所需的数字。</summary>
/// <param name="DayShift">代号 A 的班次(白班);未定义时为 null。</param>
/// <param name="NightShift">代号 B 的班次(夜班);未定义时为 null。</param>
/// <param name="PartsPerMinute">最近 5 分钟的平均节拍(件/分)。</param>
public sealed record ShiftCardStatistics(
    ShiftPeriodCount? DayShift,
    ShiftPeriodCount? NightShift,
    double PartsPerMinute);
