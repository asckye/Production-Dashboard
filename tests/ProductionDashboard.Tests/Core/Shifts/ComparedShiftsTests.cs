using FluentAssertions;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Tests.Core.Shifts;

/// <summary><see cref="ComparedShifts"/>:对比卡片取排序最靠前的两个班次,与班次代号无关。</summary>
public sealed class ComparedShiftsTests
{
    private static ShiftDefinition Def(int id, string code, int order) => new(
        id, code, code, new TimeOnly(8, 0), new TimeOnly(16, 0), false, 1000, "#fff", order,
        new DateOnly(1970, 1, 1), null, DateTime.UnixEpoch);

    [Fact]
    public void Picks_the_first_two_by_display_order()
    {
        var (first, second) = ComparedShifts.Pick([Def(5, "C", 3), Def(4, "B", 1), Def(6, "D", 2)]);

        first!.Code.Should().Be("B");
        second!.Code.Should().Be("D");
    }

    [Fact]
    public void Works_when_codes_A_and_B_no_longer_exist()
    {
        // 应用模板再删掉 A 之后,剩下的班次代号是 B、C
        var (first, second) = ComparedShifts.Pick([Def(4, "B", 2), Def(5, "C", 3)]);

        (first?.Code, second?.Code).Should().Be(("B", "C"));
    }

    [Fact]
    public void Missing_positions_are_null()
    {
        ComparedShifts.Pick([Def(1, "A", 1)]).Second.Should().BeNull();
        ComparedShifts.Pick([]).First.Should().BeNull();
    }
}
