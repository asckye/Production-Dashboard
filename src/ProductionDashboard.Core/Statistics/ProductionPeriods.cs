namespace ProductionDashboard.Core.Statistics;

/// <summary>
/// 大屏各统计维度的时间边界(本小时 / 今日 / 本周 / 本月 / 本年及其上一周期)。
/// <para>
/// 日、周、月、年按<b>班次归属日期(ShiftDate)</b>统计,用 <see cref="DateOnly"/> 表示;
/// 小时按事件发生时刻统计,用 UTC 时间表示(数据库里事件时间存的是 UTC)。
/// 一周从周一开始。
/// </para>
/// </summary>
/// <param name="Today">今天(本地日期)。</param>
/// <param name="CurrentHour">当前本地小时(0-23)。</param>
/// <param name="HourStartUtc">本小时开始时刻(本地整点换算成 UTC)。</param>
/// <param name="PreviousHourStartUtc">上一小时开始时刻(UTC)。</param>
/// <param name="WeekStart">本周一。</param>
/// <param name="MonthStart">本月 1 日。</param>
/// <param name="YearStart">本年 1 月 1 日。</param>
public sealed record ProductionPeriods(
    DateOnly Today,
    int CurrentHour,
    DateTime HourStartUtc,
    DateTime PreviousHourStartUtc,
    DateOnly WeekStart,
    DateOnly MonthStart,
    DateOnly YearStart)
{
    /// <summary>昨天。</summary>
    public DateOnly Yesterday => Today.AddDays(-1);

    /// <summary>上周一。</summary>
    public DateOnly LastWeekStart => WeekStart.AddDays(-7);

    /// <summary>上周日。</summary>
    public DateOnly LastWeekEnd => WeekStart.AddDays(-1);

    /// <summary>上月 1 日。</summary>
    public DateOnly LastMonthStart => MonthStart.AddMonths(-1);

    /// <summary>上月最后一天。</summary>
    public DateOnly LastMonthEnd => MonthStart.AddDays(-1);

    /// <summary>去年 1 月 1 日。</summary>
    public DateOnly LastYearStart => YearStart.AddYears(-1);

    /// <summary>去年 12 月 31 日。</summary>
    public DateOnly LastYearEnd => YearStart.AddDays(-1);

    /// <summary>
    /// 计算 <paramref name="localNow"/> 所在的各统计时段。
    /// </summary>
    /// <param name="localNow">当前本地时间(通常来自 <c>TimeProvider.GetLocalNow().DateTime</c>)。</param>
    /// <param name="timeZone">本地时区,用于把「本地整点」换算成 UTC。</param>
    public static ProductionPeriods For(DateTime localNow, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var today = DateOnly.FromDateTime(localNow);

        // 「本小时」以本地整点为界(而不是 UTC 整点),这样 UTC+5:30 等非整点时区也正确。
        var hourStartLocal = new DateTime(
            localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0, DateTimeKind.Unspecified);
        var hourStartUtc = TimeZoneInfo.ConvertTimeToUtc(hourStartLocal, timeZone);

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;

        return new ProductionPeriods(
            Today: today,
            CurrentHour: localNow.Hour,
            HourStartUtc: hourStartUtc,
            PreviousHourStartUtc: hourStartUtc.AddHours(-1),
            WeekStart: today.AddDays(-daysSinceMonday),
            MonthStart: new DateOnly(today.Year, today.Month, 1),
            YearStart: new DateOnly(today.Year, 1, 1));
    }
}
