using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Statistics;

/// <summary>
/// <see cref="IProductionStatisticsService"/> 的实现:按 <see cref="ProductionPeriods"/> 计算时间边界,
/// 并行查询 <see cref="IProductionAnalyticsRepository"/>,再套用 <see cref="ProductionTargetOptions"/> 的目标规则。
/// </summary>
public sealed class ProductionStatisticsService : IProductionStatisticsService
{
    private readonly IProductionAnalyticsRepository _repository;
    private readonly IShiftManager _shifts;
    private readonly ITeamAssignmentService _teams;
    private readonly ProductionTargetOptions _targets;
    private readonly TimeProvider _clock;

    /// <summary>注入数据仓库、班次与班组上下文、目标规则和时钟。</summary>
    public ProductionStatisticsService(
        IProductionAnalyticsRepository repository,
        IShiftManager shifts,
        ITeamAssignmentService teams,
        IOptions<ProductionTargetOptions> targets,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _repository = repository;
        _shifts = shifts;
        _teams = teams;
        _targets = targets.Value;
        _clock = clock;
    }

    /// <inheritdoc/>
    public async Task<DashboardStatistics> GetDashboardStatisticsAsync(CancellationToken cancellationToken)
    {
        var p = ProductionPeriods.For(_clock.GetLocalNow().DateTime, _clock.LocalTimeZone);
        var ct = cancellationToken;

        // 班组按显示顺序取前两个(内存读取,无数据库访问)
        var teams = _teams.Teams;
        var teamA = teams.Count > 0 ? teams[0] : null;
        var teamB = teams.Count > 1 ? teams[1] : null;

        // 所有查询互不依赖,并行发起:SQLite WAL 模式支持多个只读连接同时查询,
        // 总耗时约等于最慢的单个查询,而不是全部查询耗时之和。
        var hour = _repository.CountEventsInRangeAsync(p.HourStartUtc, p.HourStartUtc.AddHours(1), ct);
        var previousHour = _repository.CountEventsInRangeAsync(p.PreviousHourStartUtc, p.HourStartUtc, ct);
        var day = CountByDateAsync(p.Today, p.Today, ct);
        var yesterday = CountByDateAsync(p.Yesterday, p.Yesterday, ct);
        var week = CountByDateAsync(p.WeekStart, p.Today, ct);
        var lastWeek = CountByDateAsync(p.LastWeekStart, p.LastWeekEnd, ct);
        var month = CountByDateAsync(p.MonthStart, p.Today, ct);
        var lastMonth = CountByDateAsync(p.LastMonthStart, p.LastMonthEnd, ct);
        var year = CountByDateAsync(p.YearStart, p.Today, ct);
        var lastYear = CountByDateAsync(p.LastYearStart, p.LastYearEnd, ct);

        var teamATotals = GetTeamTotalsAsync(teamA, p, ct);
        var teamBTotals = GetTeamTotalsAsync(teamB, p, ct);

        var todayHourly = _repository.GetHourlyCountsForDateAsync(p.Today, ct);
        var yesterdayHourly = _repository.GetHourlyCountsForDateAsync(p.Yesterday, ct);
        var sevenDays = _repository.GetDailyCountsAsync(p.Today.AddDays(-6), p.Today, ct);
        var sixWeeks = _repository.GetDailyCountsAsync(p.WeekStart.AddDays(-5 * 7), p.Today, ct);
        var sixMonths = _repository.GetDailyCountsAsync(p.MonthStart.AddMonths(-5), p.Today, ct);
        var threeYears = _repository.GetDailyCountsAsync(p.YearStart.AddYears(-2), p.Today, ct);

        await Task.WhenAll(
            hour, previousHour, day, yesterday, week, lastWeek, month, lastMonth, year, lastYear,
            teamATotals, teamBTotals,
            todayHourly, yesterdayHourly, sevenDays, sixWeeks, sixMonths, threeYears).ConfigureAwait(false);

        var targets = ComputeTargets(_shifts.Definitions, _shifts.Current?.Definition, _targets);

        return new DashboardStatistics(
            Periods: p,
            Hour: new PeriodStatistics(hour.Result, previousHour.Result, targets.Hour),
            Day: new PeriodStatistics(day.Result, yesterday.Result, targets.Day),
            Week: new PeriodStatistics(week.Result, lastWeek.Result, targets.Week),
            Month: new PeriodStatistics(month.Result, lastMonth.Result, targets.Month),
            Year: new PeriodStatistics(year.Result, lastYear.Result, targets.Year),
            TeamA: teamATotals.Result,
            TeamB: teamBTotals.Result,
            TodayHourly: todayHourly.Result,
            YesterdayHourly: yesterdayHourly.Result,
            LastSevenDays: sevenDays.Result,
            LastSixWeeks: sixWeeks.Result,
            LastSixMonths: sixMonths.Result,
            LastThreeYears: threeYears.Result);
    }

    /// <inheritdoc/>
    public async Task<ShiftCardStatistics> GetShiftCardStatisticsAsync(CancellationToken cancellationToken)
    {
        var nowLocal = _clock.GetLocalNow().DateTime;
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        var definitions = _shifts.Definitions;
        var dayShift = await CountMostRecentPeriodAsync(
            definitions.FirstOrDefault(d => d.Code == "A"), nowLocal, cancellationToken).ConfigureAwait(false);
        var nightShift = await CountMostRecentPeriodAsync(
            definitions.FirstOrDefault(d => d.Code == "B"), nowLocal, cancellationToken).ConfigureAwait(false);

        // 节拍 = 最近 5 分钟产量 / 5(按事件时间,跨班次、跨日连续)
        var lastFiveMinutes = await _repository.CountEventsInRangeAsync(
            nowUtc.AddMinutes(-5), nowUtc, cancellationToken).ConfigureAwait(false);

        return new ShiftCardStatistics(dayShift, nightShift, lastFiveMinutes / 5.0);
    }

    /// <summary>
    /// 根据班次定义和换算规则计算各维度目标。
    /// 小时目标 = 当前班次目标 ÷ 当前班次时长(小时);日目标 = 所有班次目标之和;
    /// 周 / 月目标 = 日目标 × 生产天数;年目标 = 月目标 × 12。
    /// </summary>
    internal static (int Hour, int Day, int Week, int Month, int Year) ComputeTargets(
        IReadOnlyList<ShiftDefinition> definitions, ShiftDefinition? currentShift, ProductionTargetOptions options)
    {
        var day = definitions.Sum(d => d.TargetCount);
        var hour = currentShift is null || currentShift.Duration <= TimeSpan.Zero
            ? 0
            : (int)(currentShift.TargetCount / currentShift.Duration.TotalHours);
        var month = day * options.WorkingDaysPerMonth;
        return (hour, day, day * options.WorkingDaysPerWeek, month, month * 12);
    }

    private Task<int> CountByDateAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        _repository.CountEventsByShiftDateRangeAsync(from, to, null, ct);

    private async Task<TeamTotals?> GetTeamTotalsAsync(Team? team, ProductionPeriods p, CancellationToken ct)
    {
        if (team is null) return null;

        var week = _repository.CountEventsByTeamRangeAsync(p.WeekStart, p.Today, team.Id, ct);
        var month = _repository.CountEventsByTeamRangeAsync(p.MonthStart, p.Today, team.Id, ct);
        var year = _repository.CountEventsByTeamRangeAsync(p.YearStart, p.Today, team.Id, ct);
        await Task.WhenAll(week, month, year).ConfigureAwait(false);
        return new TeamTotals(team.Name, week.Result, month.Result, year.Result);
    }

    /// <summary>统计班次最近一次(或正在进行)周期的产量:进行中统计到当前时刻,已结束则统计整个周期。</summary>
    private async Task<ShiftPeriodCount?> CountMostRecentPeriodAsync(
        ShiftDefinition? definition, DateTime nowLocal, CancellationToken ct)
    {
        if (definition is null) return null;

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(definition, nowLocal);
        var count = await _repository.CountEventsInRangeAsync(
            ToUtc(start), ToUtc(isActive ? nowLocal : end), ct).ConfigureAwait(false);
        return new ShiftPeriodCount(definition, count);
    }

    private DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _clock.LocalTimeZone);
}
