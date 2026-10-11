using FluentAssertions;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Core.Statistics;

namespace ProductionDashboard.Tests.Core.Statistics;

/// <summary>各维度目标的换算规则(<see cref="ProductionStatisticsService.ComputeTargets"/>)。</summary>
public class ProductionTargetsTests
{
    private static ShiftDefinition Shift(string code, int startHour, int endHour, int target) => new(
        Id: 0,
        Code: code,
        Name: code,
        StartTime: new TimeOnly(startHour, 0),
        EndTime: new TimeOnly(endHour, 0),
        CrossesMidnight: startHour >= endHour,
        TargetCount: target,
        ColorHex: "#FFFFFF",
        DisplayOrder: 1,
        EffectiveFrom: new DateOnly(2026, 1, 1),
        EffectiveTo: null,
        CreatedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static readonly ShiftDefinition DayShift = Shift("A", 8, 20, 1500);
    private static readonly ShiftDefinition NightShift = Shift("B", 20, 8, 1200);

    [Fact]
    public void Default_rules_use_5_working_days_per_week_and_22_per_month()
    {
        var t = ProductionStatisticsService.ComputeTargets(
            [DayShift, NightShift], DayShift, new ProductionTargetOptions());

        t.Day.Should().Be(2700);
        t.Hour.Should().Be(125, "白班 1500 件 / 12 小时");
        t.Week.Should().Be(2700 * 5);
        t.Month.Should().Be(2700 * 22);
        t.Year.Should().Be(2700 * 22 * 12);
    }

    [Fact]
    public void Working_days_are_configurable_for_continuous_production()
    {
        var options = new ProductionTargetOptions { WorkingDaysPerWeek = 7, WorkingDaysPerMonth = 30 };

        var t = ProductionStatisticsService.ComputeTargets([DayShift, NightShift], NightShift, options);

        t.Week.Should().Be(2700 * 7);
        t.Month.Should().Be(2700 * 30);
        t.Year.Should().Be(2700 * 30 * 12);
    }

    [Fact]
    public void Hourly_target_uses_the_actual_length_of_the_current_shift()
    {
        var eightHourShift = Shift("C", 22, 6, 1200);

        var t = ProductionStatisticsService.ComputeTargets([eightHourShift], eightHourShift, new ProductionTargetOptions());

        t.Hour.Should().Be(150, "1200 件 / 8 小时");
    }

    [Fact]
    public void No_current_shift_means_no_hourly_target()
    {
        var t = ProductionStatisticsService.ComputeTargets([DayShift], currentShift: null, new ProductionTargetOptions());

        t.Hour.Should().Be(0);
        t.Day.Should().Be(1500);
    }
}
