using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Statistics;

namespace ProductionDashboard.ViewModels.Dashboard;

/// <summary>
/// Dashboard 的 5 维度卡(时/日/周/月/年)+ 累计对比表 + 5 张 mini 柱图 presenter。
/// 数字全部来自 Core 层的 <see cref="IProductionStatisticsService"/>;这里只负责把数字变成
/// 界面文字、百分比和柱高。由 facade 的 2 秒定时器通过 <see cref="RefreshAsync"/> 驱动。
/// </summary>
public sealed partial class AggregateMetricsPresenter : ObservableObject
{
    private const double MiniBarMaxHeight = 40.0;

    private readonly IProductionStatisticsService _statistics;
    private readonly Dispatcher _ui;

    /// <summary>注入统计服务 + UI dispatcher。仅初始化集合,数据由 <see cref="RefreshAsync"/> 触发拉取。</summary>
    public AggregateMetricsPresenter(IProductionStatisticsService statistics, Dispatcher uiDispatcher)
    {
        _statistics = statistics;
        _ui = uiDispatcher;

        CumulativeRows = new ObservableCollection<CumulativeRow>();
        HourMini = new ObservableCollection<MiniBar>();
        DayMini = new ObservableCollection<MiniBar>();
        WeekMini = new ObservableCollection<MiniBar>();
        MonthMini = new ObservableCollection<MiniBar>();
        YearMini = new ObservableCollection<MiniBar>();
    }

    // ── 5 维度 ObservableProperties ────────────────────────────────

    /// <summary>当前自然小时累计(已格式化 N0)。</summary>
    [ObservableProperty] public partial string HourCount { get; set; } = "0";
    /// <summary>"目标 X 件/时" 文本(当前班次目标 ÷ 班次时长)。</summary>
    [ObservableProperty] public partial string HourTargetText { get; set; } = "目标 —";
    /// <summary>当前小时完成度百分比(0-100,上限截断)。</summary>
    [ObservableProperty] public partial double HourPercent { get; set; }
    /// <summary>当前小时完成度文本("0.0%")。</summary>
    [ObservableProperty] public partial string HourPercentText { get; set; } = "0%";
    /// <summary>"较上小时 +X% / -X%" 趋势文本。</summary>
    [ObservableProperty] public partial string HourTrend { get; set; } = "";

    /// <summary>今日累计(按 ShiftDate 口径,确保 A + B = Day)。</summary>
    [ObservableProperty] public partial string DayCount { get; set; } = "0";
    /// <summary>"全天目标 X 件" 文本(所有班次 TargetCount 之和)。</summary>
    [ObservableProperty] public partial string DayTargetText { get; set; } = "全天目标 —";
    /// <summary>今日完成度百分比。</summary>
    [ObservableProperty] public partial double DayPercent { get; set; }
    /// <summary>今日完成度文本。</summary>
    [ObservableProperty] public partial string DayPercentText { get; set; } = "0%";
    /// <summary>"较昨日 +X% / -X%" 趋势文本。</summary>
    [ObservableProperty] public partial string DayTrend { get; set; } = "";

    /// <summary>本周累计(按 ShiftDate,周一为周起点)。</summary>
    [ObservableProperty] public partial string WeekCount { get; set; } = "0";
    /// <summary>"周目标 X 件" 文本(日目标 × 每周生产天数,见 Targets 配置)。</summary>
    [ObservableProperty] public partial string WeekTargetText { get; set; } = "周目标 —";
    /// <summary>本周完成度百分比。</summary>
    [ObservableProperty] public partial double WeekPercent { get; set; }
    /// <summary>本周完成度文本。</summary>
    [ObservableProperty] public partial string WeekPercentText { get; set; } = "0%";
    /// <summary>"较上周 +X% / -X%" 趋势文本。</summary>
    [ObservableProperty] public partial string WeekTrend { get; set; } = "";

    /// <summary>本月累计。</summary>
    [ObservableProperty] public partial string MonthCount { get; set; } = "0";
    /// <summary>"月目标 X 件" 文本(日目标 × 每月生产天数,见 Targets 配置)。</summary>
    [ObservableProperty] public partial string MonthTargetText { get; set; } = "月目标 —";
    /// <summary>本月完成度百分比。</summary>
    [ObservableProperty] public partial double MonthPercent { get; set; }
    /// <summary>本月完成度文本。</summary>
    [ObservableProperty] public partial string MonthPercentText { get; set; } = "0%";
    /// <summary>"较上月 +X% / -X%" 趋势文本。</summary>
    [ObservableProperty] public partial string MonthTrend { get; set; } = "";

    /// <summary>本年累计。</summary>
    [ObservableProperty] public partial string YearCount { get; set; } = "0";
    /// <summary>"年目标 X 件" 文本(月目标 × 12)。</summary>
    [ObservableProperty] public partial string YearTargetText { get; set; } = "年目标 —";
    /// <summary>本年完成度百分比。</summary>
    [ObservableProperty] public partial double YearPercent { get; set; }
    /// <summary>本年完成度文本。</summary>
    [ObservableProperty] public partial string YearPercentText { get; set; } = "0%";
    /// <summary>"较去年 +X% / -X%" 趋势文本。</summary>
    [ObservableProperty] public partial string YearTrend { get; set; } = "";

    // ── 累计对比 ────────────────────────────────────────────────────

    /// <summary>累计对比表的 3 行(本周 / 本月 / 本年),含 A/B 双班组的横向 mini bar 比较。</summary>
    public ObservableCollection<CumulativeRow> CumulativeRows { get; }

    /// <summary>累计对比表底部的领先汇总文本(例 "A 班 两 项领先")。</summary>
    [ObservableProperty] public partial string CumulativeLeaderText { get; set; } = "";
    /// <summary>领先方标识 — "A" / "B" / "T"(并列)。UI 用此切换徽章颜色。</summary>
    [ObservableProperty] public partial string CumulativeLeaderKind { get; set; } = "T";

    /// <summary>本月对比的领先总结(顶栏 / Hero 区显示),例 "本月 A 班 领先 +123 件 (+5.0%)"。</summary>
    [ObservableProperty] public partial string LeaderText { get; set; } = "—";

    // ── Mini 柱图 ──────────────────────────────────────────────────

    /// <summary>小时维度的 mini 柱图(过去 6 小时,含本小时)。</summary>
    public ObservableCollection<MiniBar> HourMini { get; }
    /// <summary>日维度的 mini 柱图(过去 7 天,含今天)。</summary>
    public ObservableCollection<MiniBar> DayMini { get; }
    /// <summary>周维度的 mini 柱图(过去 6 周,含本周)。</summary>
    public ObservableCollection<MiniBar> WeekMini { get; }
    /// <summary>月维度的 mini 柱图(过去 6 月,含本月)。</summary>
    public ObservableCollection<MiniBar> MonthMini { get; }
    /// <summary>年维度的 mini 柱图(过去 3 年,含本年)。</summary>
    public ObservableCollection<MiniBar> YearMini { get; }

    // ── 周期性聚合 ─────────────────────────────────────────────────

    /// <summary>2 秒一次的聚合刷新:后台取数 + 计算,UI 线程只做赋值。</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var stats = await _statistics.GetDashboardStatisticsAsync(ct).ConfigureAwait(false);
        var p = stats.Periods;

        // ── 后台计算(纯函数,在线程池线程跑,不碰 UI 线程也不碰集合) ──
        var teamAName = stats.TeamA?.TeamName ?? "A 班";
        var teamBName = stats.TeamB?.TeamName ?? "B 班";
        var a = stats.TeamA ?? new TeamTotals(teamAName, 0, 0, 0);
        var b = stats.TeamB ?? new TeamTotals(teamBName, 0, 0, 0);
        var cumRows = new[]
        {
            BuildCumulativeRow("本周", a.Week, b.Week, teamAName, teamBName),
            BuildCumulativeRow("本月", a.Month, b.Month, teamAName, teamBName),
            BuildCumulativeRow("本年", a.Year, b.Year, teamAName, teamBName),
        };
        var (leaderText, leaderKind) = ComputeCumulativeLeader(
            a.Week, b.Week, a.Month, b.Month, a.Year, b.Year, teamAName, teamBName);
        var monthLeaderText = ComputeMonthLeaderText(a.Month, b.Month, teamAName, teamBName);

        var hourBars = ComputeHourBars(stats.YesterdayHourly, stats.TodayHourly, p.CurrentHour);
        var dayBars = ComputeDayBars(p.Today.AddDays(-6), p.Today, stats.LastSevenDays);
        var weekBars = ComputeWeekBars(p.WeekStart, stats.LastSixWeeks);
        var monthBars = ComputeMonthBars(p.MonthStart, stats.LastSixMonths);
        var yearBars = ComputeYearBars(p.Today.Year, stats.LastThreeYears);

        // ── UI 线程推送:只做属性赋值 + 集合就地同步(无 LINQ、无 Clear 重建) ──
        await _ui.InvokeAsync(() =>
        {
            ApplyDimensions(stats);

            SyncList(CumulativeRows, cumRows);
            CumulativeLeaderText = leaderText;
            CumulativeLeaderKind = leaderKind;
            LeaderText = monthLeaderText;

            SyncList(HourMini, hourBars);
            SyncList(DayMini, dayBars);
            SyncList(WeekMini, weekBars);
            SyncList(MonthMini, monthBars);
            SyncList(YearMini, yearBars);
        });
    }

    // ── 5 维度数字 + 百分比 ─────────────────────────────────────────

    private void ApplyDimensions(DashboardStatistics stats)
    {
        HourCount = stats.Hour.Actual.ToString("N0");
        HourTargetText = TargetText("目标", stats.Hour.Target, "件/时");
        HourPercent = stats.Hour.CompletionPercent;
        HourPercentText = $"{HourPercent:F1}%";
        HourTrend = TrendFormatter.FormatTrend(stats.Hour.Actual, stats.Hour.Previous, "上小时");

        DayCount = stats.Day.Actual.ToString("N0");
        DayTargetText = TargetText("全天目标", stats.Day.Target, "件");
        DayPercent = stats.Day.CompletionPercent;
        DayPercentText = $"{DayPercent:F1}%";
        DayTrend = TrendFormatter.FormatTrend(stats.Day.Actual, stats.Day.Previous, "昨日");

        WeekCount = stats.Week.Actual.ToString("N0");
        WeekTargetText = TargetText("周目标", stats.Week.Target, "件");
        WeekPercent = stats.Week.CompletionPercent;
        WeekPercentText = $"{WeekPercent:F1}%";
        WeekTrend = TrendFormatter.FormatTrend(stats.Week.Actual, stats.Week.Previous, "上周");

        MonthCount = stats.Month.Actual.ToString("N0");
        MonthTargetText = TargetText("月目标", stats.Month.Target, "件");
        MonthPercent = stats.Month.CompletionPercent;
        MonthPercentText = $"{MonthPercent:F1}%";
        MonthTrend = TrendFormatter.FormatTrend(stats.Month.Actual, stats.Month.Previous, "上月");

        YearCount = stats.Year.Actual.ToString("N0");
        YearTargetText = TargetText("年目标", stats.Year.Target, "件");
        YearPercent = stats.Year.CompletionPercent;
        YearPercentText = $"{YearPercent:F1}%";
        YearTrend = TrendFormatter.FormatTrend(stats.Year.Actual, stats.Year.Previous, "去年");
    }

    /// <summary>目标文本,例如 "周目标 13,500 件";未设置目标时显示 "周目标 —"。</summary>
    internal static string TargetText(string label, int target, string unit) =>
        target > 0 ? $"{label} {target:N0} {unit}" : $"{label} —";

    // ── 累计对比表 ─────────────────────────────────────────────────

    internal static (string Text, string Kind) ComputeCumulativeLeader(
        int aWeek, int bWeek, int aMonth, int bMonth, int aYear, int bYear,
        string teamAName, string teamBName)
    {
        var aWins = 0;
        var bWins = 0;
        if (aWeek > bWeek) aWins++; else if (bWeek > aWeek) bWins++;
        if (aMonth > bMonth) aWins++; else if (bMonth > aMonth) bWins++;
        if (aYear > bYear) aWins++; else if (bYear > aYear) bWins++;

        string ChinaCount(int n) => n switch
        {
            1 => "一", 2 => "两", 3 => "三", _ => n.ToString()
        };

        return (aWins, bWins) switch
        {
            (0, 0) => ("三项持平", "T"),
            (_, 0) => ($"{teamAName} {ChinaCount(aWins)} 项领先", "A"),
            (0, _) => ($"{teamBName} {ChinaCount(bWins)} 项领先", "B"),
            var (a, b) when a > b => ($"{teamAName} {ChinaCount(a)} 项领先 · {teamBName} {ChinaCount(b)} 项", "A"),
            var (a, b) when b > a => ($"{teamBName} {ChinaCount(b)} 项领先 · {teamAName} {ChinaCount(a)} 项", "B"),
            _ => ($"{teamAName} {aWins} : {teamBName} {bWins} 平", "T"),
        };
    }

    internal static CumulativeRow BuildCumulativeRow(
        string label, int a, int b, string teamAName, string teamBName)
    {
        var max = Math.Max(Math.Max(a, b), 1); // 防 0 除
        var aPct = a * 100.0 / max;
        var bPct = b * 100.0 / max;
        var diff = a - b;
        var aLeads = diff >= 0;
        var diffText = diff switch
        {
            > 0 => $"{teamAName} +{diff:N0}",
            < 0 => $"{teamBName} +{-diff:N0}",
            _ => "持平",
        };
        var basis = aLeads ? b : a;
        var diffPctText = basis == 0
            ? (diff == 0 ? "—" : "+∞%")
            : $"{(Math.Abs(diff) * 100.0 / basis):+0.0;-0.0;0}%";
        return new CumulativeRow(label, aPct, bPct,
            a.ToString("N0"), b.ToString("N0"),
            diffText, diffPctText, aLeads);
    }

    internal static string ComputeMonthLeaderText(
        int aMonth, int bMonth, string teamAName, string teamBName)
    {
        var diff = aMonth - bMonth;
        if (diff == 0)
        {
            return bMonth == 0 ? "本月暂无数据" : $"本月 {teamAName} / {teamBName} 持平";
        }
        var basis = diff > 0 ? bMonth : aMonth;
        var pct = basis == 0
            ? ""
            : $" ({(Math.Abs(diff) * 100.0 / basis):+0.0;-0.0;0}%)";
        var (leader, gap) = diff > 0 ? (teamAName, diff) : (teamBName, -diff);
        return $"本月 {leader} 领先 +{gap:N0} 件{pct}";
    }

    // ── Mini 柱图填充 ──────────────────────────────────────────────

    internal static IReadOnlyList<MiniBar> ComputeHourBars(
        IReadOnlyList<int> yesterdayHours, IReadOnlyList<int> todayHours, int currentHour)
    {
        var combined = new int[48];
        for (var i = 0; i < 24; i++)
        {
            combined[i] = yesterdayHours.Count > i ? yesterdayHours[i] : 0;
            combined[i + 24] = todayHours.Count > i ? todayHours[i] : 0;
        }

        var endIdx = 24 + currentHour;
        var startIdx = Math.Max(0, endIdx - 5);
        var slice = combined.Skip(startIdx).Take(endIdx - startIdx + 1).ToArray();
        var max = Math.Max(1, slice.Max());

        var bars = new List<MiniBar>(slice.Length);
        for (var i = 0; i < slice.Length; i++)
        {
            var rawHour = (startIdx + i) % 24;
            bars.Add(new MiniBar(
                Label: rawHour.ToString("D2"),
                Height: slice[i] * MiniBarMaxHeight / max,
                IsToday: (startIdx + i) == endIdx));
        }
        return bars;
    }

    internal static IReadOnlyList<MiniBar> ComputeDayBars(
        DateOnly fromDate, DateOnly today, IReadOnlyList<DailyCount> rows)
    {
        var byDate = rows.ToDictionary(r => r.Date, r => r.Count);
        var max = 1;
        for (var d = fromDate; d <= today; d = d.AddDays(1))
        {
            max = Math.Max(max, byDate.TryGetValue(d, out var v) ? v : 0);
        }

        var bars = new List<MiniBar>();
        for (var d = fromDate; d <= today; d = d.AddDays(1))
        {
            var count = byDate.TryGetValue(d, out var c) ? c : 0;
            var isToday = d == today;
            bars.Add(new MiniBar(
                Label: isToday ? "今天" : DayOfWeekShort(d.DayOfWeek),
                Height: count * MiniBarMaxHeight / max,
                IsToday: isToday));
        }
        return bars;
    }

    internal static IReadOnlyList<MiniBar> ComputeWeekBars(DateOnly weekStart, IReadOnlyList<DailyCount> rows)
    {
        var weekBuckets = new int[6];
        var weekLabels = new string[6];
        for (var w = 0; w < 6; w++)
        {
            var bucketStart = weekStart.AddDays(-w * 7);
            var bucketEnd = bucketStart.AddDays(6);
            weekBuckets[5 - w] = rows
                .Where(r => r.Date >= bucketStart && r.Date <= bucketEnd)
                .Sum(r => r.Count);
            weekLabels[5 - w] = w == 0
                ? "本周"
                : $"W{System.Globalization.ISOWeek.GetWeekOfYear(bucketStart.ToDateTime(TimeOnly.MinValue))}";
        }

        var max = Math.Max(1, weekBuckets.Max());
        var bars = new List<MiniBar>(6);
        for (var i = 0; i < 6; i++)
        {
            bars.Add(new MiniBar(
                Label: weekLabels[i],
                Height: weekBuckets[i] * MiniBarMaxHeight / max,
                IsToday: i == 5));
        }
        return bars;
    }

    internal static IReadOnlyList<MiniBar> ComputeMonthBars(DateOnly currentMonthStart, IReadOnlyList<DailyCount> rows)
    {
        var buckets = new int[6];
        var labels = new string[6];
        for (var m = 0; m < 6; m++)
        {
            var bStart = currentMonthStart.AddMonths(-m);
            var bEnd = bStart.AddMonths(1).AddDays(-1);
            buckets[5 - m] = rows
                .Where(r => r.Date >= bStart && r.Date <= bEnd)
                .Sum(r => r.Count);
            labels[5 - m] = m == 0
                ? "本月"
                : (bStart.Year != currentMonthStart.Year
                    ? $"去{bStart.Month:D2}"
                    : bStart.Month.ToString("D2"));
        }

        var max = Math.Max(1, buckets.Max());
        var bars = new List<MiniBar>(6);
        for (var i = 0; i < 6; i++)
        {
            bars.Add(new MiniBar(
                Label: labels[i],
                Height: buckets[i] * MiniBarMaxHeight / max,
                IsToday: i == 5));
        }
        return bars;
    }

    internal static IReadOnlyList<MiniBar> ComputeYearBars(int currentYear, IReadOnlyList<DailyCount> rows)
    {
        var buckets = new int[3];
        var labels = new string[3];
        for (var y = 0; y < 3; y++)
        {
            var year = currentYear - (2 - y);
            buckets[y] = rows
                .Where(r => r.Date.Year == year)
                .Sum(r => r.Count);
            labels[y] = year.ToString();
        }

        var max = Math.Max(1, buckets.Max());
        var bars = new List<MiniBar>(3);
        for (var i = 0; i < 3; i++)
        {
            bars.Add(new MiniBar(
                Label: labels[i],
                Height: buckets[i] * MiniBarMaxHeight / max,
                IsToday: i == 2));
        }
        return bars;
    }

    private static string DayOfWeekShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>
    /// 就地把 <paramref name="source"/> 同步到 <paramref name="target"/>:逐索引替换(触发 Replace,
    /// 容器复用)+ 尾部增删。避免 Clear()(Reset 会销毁全部容器)+ Add 的整列表重建,
    /// 降低 2 秒刷新时 ItemsControl 的布局/容器开销。须在 UI 线程调用。
    /// </summary>
    private static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count) target[i] = source[i];
            else target.Add(source[i]);
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
}

/// <summary>累计对比一行(本周/本月/本年)。</summary>
public sealed record CumulativeRow(
    string Label,
    double APct,
    double BPct,
    string ACountText,
    string BCountText,
    string DiffText,
    string DiffPctText,
    bool ALeads);

/// <summary>5 维度卡的 mini 柱图的一根柱。Height 已按 series 内 max 归一化到像素。</summary>
public sealed record MiniBar(string Label, double Height, bool IsToday);
