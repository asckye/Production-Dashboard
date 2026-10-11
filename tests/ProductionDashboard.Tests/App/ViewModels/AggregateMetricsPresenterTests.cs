using FluentAssertions;
using ProductionDashboard.Core.Production;
using ProductionDashboard.ViewModels.Dashboard;

namespace ProductionDashboard.Tests.App.ViewModels;

/// <summary>
/// AggregateMetricsPresenter 的纯计算函数测试。这些函数(B 档重构后)从 UI 线程剥离为
/// internal static,使原本零测试的 mini 柱图分桶 / 领先判定逻辑获得回归保护。
/// </summary>
public sealed class AggregateMetricsPresenterTests
{
    // ── mini 柱图分桶 ──────────────────────────────────────────────

    [Fact]
    public void ComputeYearBars_buckets_by_year_normalizes_height_marks_current()
    {
        var rows = new[]
        {
            new DailyCount(new DateOnly(2024, 3, 1), 10),
            new DailyCount(new DateOnly(2024, 6, 1), 20),   // 2024 合计 30
            new DailyCount(new DateOnly(2025, 1, 1), 50),   // 2025 = 50
            new DailyCount(new DateOnly(2026, 1, 1), 100),  // 2026 = 100(当前年)
        };

        var bars = AggregateMetricsPresenter.ComputeYearBars(2026, rows);

        bars.Should().HaveCount(3);
        bars[0].Label.Should().Be("2024");
        bars[2].Label.Should().Be("2026");
        bars[2].IsToday.Should().BeTrue();
        bars[0].IsToday.Should().BeFalse();
        // 归一化到 max=100:2026 → 40px(满高),2024(30) → 12px
        bars[2].Height.Should().BeApproximately(40.0, 0.01);
        bars[0].Height.Should().BeApproximately(30 * 40.0 / 100, 0.01);
    }

    [Fact]
    public void ComputeWeekBars_has_six_buckets_last_is_current_week()
    {
        var weekStart = new DateOnly(2026, 5, 25); // 周一
        var rows = new[] { new DailyCount(new DateOnly(2026, 5, 26), 42) };

        var bars = AggregateMetricsPresenter.ComputeWeekBars(weekStart, rows);

        bars.Should().HaveCount(6);
        bars[5].Label.Should().Be("本周");
        bars[5].IsToday.Should().BeTrue();
    }

    [Fact]
    public void ComputeDayBars_marks_today_and_fills_gaps_with_zero()
    {
        var from = new DateOnly(2026, 5, 25);
        var today = new DateOnly(2026, 5, 27); // 3 天窗口
        var rows = new[] { new DailyCount(new DateOnly(2026, 5, 25), 10) }; // 26/27 无数据

        var bars = AggregateMetricsPresenter.ComputeDayBars(from, today, rows);

        bars.Should().HaveCount(3);
        bars[^1].IsToday.Should().BeTrue();
        bars[^1].Label.Should().Be("今天");
        bars[^1].Height.Should().Be(0, "今天无产量");
    }

    // ── 领先判定 ────────────────────────────────────────────────────

    [Fact]
    public void ComputeCumulativeLeader_team_a_three_leads()
    {
        var (text, kind) = AggregateMetricsPresenter.ComputeCumulativeLeader(
            aWeek: 10, bWeek: 5, aMonth: 20, bMonth: 8, aYear: 100, bYear: 60, "甲班", "乙班");

        kind.Should().Be("A");
        text.Should().Contain("甲班").And.Contain("三");
    }

    [Fact]
    public void ComputeCumulativeLeader_all_tied()
    {
        var (text, kind) = AggregateMetricsPresenter.ComputeCumulativeLeader(
            5, 5, 5, 5, 5, 5, "甲班", "乙班");

        kind.Should().Be("T");
        text.Should().Be("三项持平");
    }

    [Fact]
    public void ComputeCumulativeLeader_split_a_two_b_one()
    {
        var (text, kind) = AggregateMetricsPresenter.ComputeCumulativeLeader(
            aWeek: 10, bWeek: 5,    // A
            aMonth: 5, bMonth: 10,  // B
            aYear: 100, bYear: 60,  // A
            "甲班", "乙班");

        kind.Should().Be("A", "甲 2 项 > 乙 1 项");
    }

    // ── 本月领先文本 ────────────────────────────────────────────────

    [Fact]
    public void ComputeMonthLeaderText_no_data()
    {
        AggregateMetricsPresenter.ComputeMonthLeaderText(0, 0, "甲班", "乙班")
            .Should().Be("本月暂无数据");
    }

    [Fact]
    public void ComputeMonthLeaderText_team_a_leads_shows_gap()
    {
        var text = AggregateMetricsPresenter.ComputeMonthLeaderText(120, 100, "甲班", "乙班");

        text.Should().Contain("甲班").And.Contain("+20");
    }

    [Fact]
    public void ComputeMonthLeaderText_tie_with_data()
    {
        AggregateMetricsPresenter.ComputeMonthLeaderText(50, 50, "甲班", "乙班")
            .Should().Contain("持平");
    }
}
