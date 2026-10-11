using FluentAssertions;
using ProductionDashboard.ViewModels.Dashboard;

namespace ProductionDashboard.Tests.App.ViewModels;

/// <summary>
/// 5 维度卡的趋势文本格式化测试。重点覆盖:
/// <list type="bullet">
///   <item>previous == 0 的两种分支(current == 0 vs &gt; 0)</item>
///   <item>正增长 / 负增长 / 持平</item>
///   <item>千分位格式 "+0.0;-0.0;0" 在不同符号下的输出</item>
/// </list>
/// </summary>
public sealed class TrendFormatterTests
{
    [Theory]
    [InlineData(0, 0, "上小时", "—")]
    [InlineData(0, 0, "昨日", "—")]
    public void Returns_dash_when_both_current_and_previous_are_zero(
        int current, int previous, string label, string expected)
    {
        TrendFormatter.FormatTrend(current, previous, label).Should().Be(expected);
    }

    [Theory]
    [InlineData(10, 0, "上小时", "比上小时 新增")]
    [InlineData(1, 0, "昨日", "比昨日 新增")]
    public void Returns_new_when_previous_zero_and_current_positive(
        int current, int previous, string label, string expected)
    {
        TrendFormatter.FormatTrend(current, previous, label).Should().Be(expected);
    }

    [Fact]
    public void Positive_growth_uses_up_arrow_and_plus_sign()
    {
        var result = TrendFormatter.FormatTrend(150, 100, "上周");
        result.Should().Be("↑ 比上周 +50.0%");
    }

    [Fact]
    public void Negative_growth_uses_down_arrow_and_minus_sign()
    {
        var result = TrendFormatter.FormatTrend(60, 100, "上月");
        result.Should().Be("↓ 比上月 -40.0%");
    }

    [Fact]
    public void Flat_uses_up_arrow_and_zero()
    {
        // delta == 0 → arrow = "↑"(>=0),格式化输出 "0"(中性桶)
        var result = TrendFormatter.FormatTrend(100, 100, "去年");
        result.Should().Be("↑ 比去年 0%");
    }

    [Fact]
    public void Small_percent_change_rounds_to_one_decimal()
    {
        var result = TrendFormatter.FormatTrend(103, 100, "上周");
        result.Should().Be("↑ 比上周 +3.0%");
    }
}
