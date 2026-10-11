
namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 纯函数式的班次判定。输入班次定义集合 + 一个本地时刻,
/// 输出这个时刻所属的 <see cref="ShiftAssignment"/>,无副作用、不依赖时钟、可放心单测。
/// </summary>
public static class ShiftCalculator
{
    /// <summary>
    /// 判定 <paramref name="localTime"/> 属于哪个班次。
    /// 班次定义里 <c>StartTime/EndTime</c> 用 "HH:mm" 表示本地时刻。
    /// 如果没有任何班次覆盖该时刻,返回 <c>null</c>(代表产线在空档期)。
    /// </summary>
    public static ShiftAssignment? Compute(
        IReadOnlyList<ShiftDefinition> definitions,
        DateTime localTime,
        ShiftAttributionRule attributionRule)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0) return null;

        // 班次定义里至少要 EffectiveFrom <= localTime 才有效;调用方负责传入"今天有效"的集合。
        // 这里直接遍历找匹配。
        var today = DateOnly.FromDateTime(localTime);
        var yesterday = today.AddDays(-1);

        foreach (var shift in definitions)
        {
            var start = shift.StartTime;
            var end = shift.EndTime;

            if (!shift.CrossesMidnight)
            {
                // 同日班次:8:00 -> 20:00 这种
                var periodStart = today.ToDateTime(start);
                var periodEnd = today.ToDateTime(end);
                if (localTime >= periodStart && localTime < periodEnd)
                {
                    return new ShiftAssignment(shift, today, periodStart, periodEnd);
                }
            }
            else
            {
                // 跨日班次:20:00 -> 次日 08:00 这种,要检查两种可能性

                // 情况 1: 班次今天开始,跨到明天
                // localTime in [today 20:00, tomorrow 08:00)
                var startToday = today.ToDateTime(start);
                var endTomorrow = today.AddDays(1).ToDateTime(end);
                if (localTime >= startToday && localTime < endTomorrow)
                {
                    // 结束在 00:00(例如 16:00–24:00)的班次整段都在开始那一天,归属开始日;
                    // 按"结束日"规则会被算到第二天,与现场"今天的晚班"不符
                    var endsAtMidnight = end == TimeOnly.MinValue;
                    var shiftDate = attributionRule == ShiftAttributionRule.StartDate || endsAtMidnight
                        ? today
                        : today.AddDays(1);
                    return new ShiftAssignment(shift, shiftDate, startToday, endTomorrow);
                }

                // 情况 2: 班次昨天开始,正在今天的凌晨段
                // localTime in [yesterday 20:00, today 08:00)
                var startYesterday = yesterday.ToDateTime(start);
                var endToday = today.ToDateTime(end);
                if (localTime >= startYesterday && localTime < endToday)
                {
                    var shiftDate = attributionRule == ShiftAttributionRule.StartDate
                        ? yesterday
                        : today;
                    return new ShiftAssignment(shift, shiftDate, startYesterday, endToday);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 找该班次定义"最近一次或正在进行的"周期。
    /// <list type="bullet">
    ///   <item>该班次当前正在进行 → 返回当前周期 + <c>IsActive=true</c></item>
    ///   <item>该班次今天已经结束(或不跨日的还没开始)→ 返回最近一次完整周期 + <c>IsActive=false</c></item>
    ///   <item>跨日班次还未开始的次日凌晨段 → 返回正在进行的(即 IsActive=true)</item>
    /// </list>
    /// 用于 Dashboard A 卡 / B 卡按"班次时段"切片显示——每个班次结束时不归零,
    /// 而是保持终态显示,直到下一次同名班次开始时再归零。
    /// </summary>
    public static (DateTime PeriodStart, DateTime PeriodEnd, bool IsActive) GetMostRecentPeriod(
        ShiftDefinition shift,
        DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(shift);

        var start = shift.StartTime;
        var end = shift.EndTime;
        var today = DateOnly.FromDateTime(localTime);
        var yesterday = today.AddDays(-1);

        if (!shift.CrossesMidnight)
        {
            // 同日班次:8:00 -> 20:00 这种
            var todayStart = today.ToDateTime(start);
            var todayEnd = today.ToDateTime(end);

            if (localTime >= todayStart && localTime < todayEnd)
            {
                return (todayStart, todayEnd, true);            // 正在进行
            }
            if (localTime >= todayEnd)
            {
                return (todayStart, todayEnd, false);           // 今天已结束
            }
            // localTime < todayStart:今天还没开始 → 用昨天的(刚结束)
            var ystStart = yesterday.ToDateTime(start);
            var ystEnd = yesterday.ToDateTime(end);
            return (ystStart, ystEnd, false);
        }

        // 跨日班次:20:00 -> 次日 08:00 这种
        // 周期 1:[yesterday 20:00, today 08:00) — 昨晚那段
        // 周期 2:[today 20:00, tomorrow 08:00) — 今晚那段
        var todayMorning = today.ToDateTime(end);
        var todayEvening = today.ToDateTime(start);
        var tomorrowMorning = today.AddDays(1).ToDateTime(end);
        var yesterdayEvening = yesterday.ToDateTime(start);

        if (localTime >= todayEvening)
        {
            // 今晚开始的正在进行
            return (todayEvening, tomorrowMorning, true);
        }
        if (localTime < todayMorning)
        {
            // 昨晚开始的正在进行(凌晨段)
            return (yesterdayEvening, todayMorning, true);
        }
        // todayMorning <= localTime < todayEvening:上一次刚结束 = 昨晚开始的那次
        return (yesterdayEvening, todayMorning, false);
    }
}
