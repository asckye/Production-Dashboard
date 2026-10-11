using FluentAssertions;
using ProductionDashboard.Core.Statistics;

namespace ProductionDashboard.Tests.Core.Statistics;

/// <summary><see cref="ProductionPeriods"/> 时间边界计算:本小时以本地整点为界、一周从周一开始、跨月跨年。</summary>
public class ProductionPeriodsTests
{
    private static readonly TimeZoneInfo Utc8 =
        TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static readonly TimeZoneInfo Utc530 =
        TimeZoneInfo.CreateCustomTimeZone("UTC+5:30", TimeSpan.FromMinutes(330), "UTC+5:30", "UTC+5:30");

    [Fact]
    public void Hour_window_starts_at_local_full_hour_in_whole_hour_time_zone()
    {
        var p = ProductionPeriods.For(new DateTime(2026, 10, 11, 13, 25, 0), Utc8);

        p.CurrentHour.Should().Be(13);
        p.HourStartUtc.Should().Be(new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc));
        p.PreviousHourStartUtc.Should().Be(new DateTime(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Hour_window_starts_at_local_full_hour_in_half_hour_time_zone()
    {
        // 13:25 (UTC+5:30) → 本小时从本地 13:00 开始 = 07:30 UTC。
        // 若按 UTC 整点截断,会错误地从本地 13:30 开始。
        var p = ProductionPeriods.For(new DateTime(2026, 10, 11, 13, 25, 0), Utc530);

        p.HourStartUtc.Should().Be(new DateTime(2026, 10, 11, 7, 30, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(11, 5)]   // 2026-10-11 周日 → 本周一 10-05
    [InlineData(12, 12)]  // 周一 → 当天
    [InlineData(14, 12)]  // 周三 → 10-12
    public void Week_starts_on_monday(int day, int expectedMonday)
    {
        var p = ProductionPeriods.For(new DateTime(2026, 10, day, 9, 0, 0), Utc8);

        var monday = new DateOnly(2026, 10, expectedMonday);
        p.WeekStart.Should().Be(monday);
        p.LastWeekStart.Should().Be(monday.AddDays(-7));
        p.LastWeekEnd.Should().Be(monday.AddDays(-1));
    }

    [Fact]
    public void Month_and_year_boundaries_cover_previous_periods()
    {
        var p = ProductionPeriods.For(new DateTime(2028, 3, 15, 9, 0, 0), Utc8);

        p.Yesterday.Should().Be(new DateOnly(2028, 3, 14));
        p.MonthStart.Should().Be(new DateOnly(2028, 3, 1));
        p.LastMonthStart.Should().Be(new DateOnly(2028, 2, 1));
        p.LastMonthEnd.Should().Be(new DateOnly(2028, 2, 29), "2028 是闰年");
        p.YearStart.Should().Be(new DateOnly(2028, 1, 1));
        p.LastYearStart.Should().Be(new DateOnly(2027, 1, 1));
        p.LastYearEnd.Should().Be(new DateOnly(2027, 12, 31));
    }
}
