using FluentAssertions;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Tests.Core.Shifts;

/// <summary><see cref="ShiftDefinition.Duration"/>:同日、跨日与全天班次的时长。</summary>
public class ShiftDefinitionTests
{
    [Theory]
    [InlineData(8, 20, 12)]   // 白班
    [InlineData(20, 8, 12)]   // 跨日夜班
    [InlineData(22, 6, 8)]    // 三班倒的夜班
    [InlineData(0, 0, 24)]    // 单班全天
    public void Duration_handles_same_day_overnight_and_full_day_shifts(int startHour, int endHour, int expectedHours)
    {
        var shift = new ShiftDefinition(
            Id: 1,
            Code: "A",
            Name: "班次",
            StartTime: new TimeOnly(startHour, 0),
            EndTime: new TimeOnly(endHour, 0),
            CrossesMidnight: startHour >= endHour,
            TargetCount: 1000,
            ColorHex: "#FFFFFF",
            DisplayOrder: 1,
            EffectiveFrom: new DateOnly(2026, 1, 1),
            EffectiveTo: null,
            CreatedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        shift.Duration.Should().Be(TimeSpan.FromHours(expectedHours));
    }
}
