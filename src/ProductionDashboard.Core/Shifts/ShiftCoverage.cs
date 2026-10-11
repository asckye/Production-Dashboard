namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 一组班次在一天 24 小时里的覆盖情况。由 <see cref="ShiftCoverage.Analyze"/> 计算。
/// </summary>
/// <param name="GapMinutes">没有任何班次的分钟数。这段时间的产量不归属任何班次,会被丢弃。</param>
/// <param name="OverlapMinutes">被两个及以上班次同时覆盖的分钟数。</param>
/// <param name="FirstOverlap">第一处重叠:两个班次的代号和重叠开始的时刻;没有重叠时为 null。</param>
public sealed record ShiftCoverageResult(
    int GapMinutes,
    int OverlapMinutes,
    (string First, string Second, TimeOnly At)? FirstOverlap)
{
    /// <summary>是否有重叠。重叠时段只会算给排在前面的班次,属于配置错误,不允许保存。</summary>
    public bool HasOverlap => OverlapMinutes > 0;

    /// <summary>24 小时全部被覆盖且没有重叠。</summary>
    public bool IsComplete => GapMinutes == 0 && OverlapMinutes == 0;
}

/// <summary>
/// 班次覆盖检测:逐分钟统计一天 1440 分钟各被几个班次覆盖。
/// <para>
/// 不能只把各班次时长加起来和 24 小时比较:例如 00:00–12:00 加 06:00–18:00 合计正好 24 小时,
/// 实际上既有 6 小时重叠又有 6 小时空档。
/// </para>
/// <para>
/// 规则与 <see cref="ShiftCalculator"/> 一致:结束时刻小于等于开始时刻视为跨日,开始等于结束视为整 24 小时。
/// </para>
/// </summary>
public static class ShiftCoverage
{
    private const int MinutesPerDay = 24 * 60;

    /// <summary>分析一组班次(代号 + 起止时刻)的覆盖情况。</summary>
    public static ShiftCoverageResult Analyze(IEnumerable<(string Code, TimeOnly Start, TimeOnly End)> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);

        var owner = new string?[MinutesPerDay];
        var count = new int[MinutesPerDay];
        (string, string, TimeOnly)? firstOverlap = null;

        foreach (var (code, start, end) in shifts)
        {
            var startMinute = start.Hour * 60 + start.Minute;
            var length = DurationMinutes(start, end);
            for (var i = 0; i < length; i++)
            {
                var minute = (startMinute + i) % MinutesPerDay;
                count[minute]++;
                if (owner[minute] is null)
                {
                    owner[minute] = code;
                }
                else if (firstOverlap is null)
                {
                    firstOverlap = (owner[minute]!, code, new TimeOnly(minute / 60, minute % 60));
                }
            }
        }

        return new ShiftCoverageResult(
            GapMinutes: count.Count(c => c == 0),
            OverlapMinutes: count.Count(c => c > 1),
            FirstOverlap: firstOverlap);
    }

    /// <summary>
    /// 班次时长(分钟)。结束时刻小于等于开始时刻时视为跨日,相等时为整 24 小时。
    /// </summary>
    public static int DurationMinutes(TimeOnly start, TimeOnly end)
    {
        var diff = (end.Hour * 60 + end.Minute) - (start.Hour * 60 + start.Minute);
        return diff <= 0 ? diff + MinutesPerDay : diff;
    }
}
