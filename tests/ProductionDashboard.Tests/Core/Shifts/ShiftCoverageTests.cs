using FluentAssertions;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Tests.Core.Shifts;

/// <summary><see cref="ShiftCoverage"/>:逐分钟统计一天 24 小时的班次覆盖,准确给出空档与重叠。</summary>
public sealed class ShiftCoverageTests
{
    private static (string, TimeOnly, TimeOnly) S(string code, int startHour, int endHour) =>
        (code, new TimeOnly(startHour, 0), new TimeOnly(endHour % 24, 0));

    [Fact]
    public void Two_twelve_hour_shifts_cover_the_whole_day()
    {
        var result = ShiftCoverage.Analyze([S("A", 8, 20), S("B", 20, 8)]);

        result.IsComplete.Should().BeTrue();
        result.FirstOverlap.Should().BeNull();
    }

    [Fact]
    public void Three_shift_template_ending_at_midnight_covers_the_whole_day()
    {
        var result = ShiftCoverage.Analyze([S("A", 0, 8), S("B", 8, 16), S("C", 16, 24)]);

        result.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Durations_adding_up_to_24_hours_can_still_overlap_and_leave_a_gap()
    {
        // 00–12 + 06–18 合计 24 小时,但 06–12 重叠、18–24 空档:不能只比较总时长
        var result = ShiftCoverage.Analyze([S("A", 0, 12), S("B", 6, 18)]);

        result.OverlapMinutes.Should().Be(6 * 60);
        result.GapMinutes.Should().Be(6 * 60);
        result.FirstOverlap.Should().Be(("A", "B", new TimeOnly(6, 0)));
        result.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Single_shift_line_has_a_gap_but_no_overlap()
    {
        var result = ShiftCoverage.Analyze([S("A", 8, 17)]);

        result.GapMinutes.Should().Be(15 * 60);
        result.HasOverlap.Should().BeFalse();
    }

    [Fact]
    public void Overlap_across_midnight_is_detected()
    {
        var result = ShiftCoverage.Analyze([S("A", 20, 8), S("B", 6, 20)]);

        result.OverlapMinutes.Should().Be(2 * 60);
        result.GapMinutes.Should().Be(0);
    }

    [Fact]
    public void No_shifts_means_the_whole_day_is_a_gap()
    {
        ShiftCoverage.Analyze([]).GapMinutes.Should().Be(24 * 60);
    }
}
