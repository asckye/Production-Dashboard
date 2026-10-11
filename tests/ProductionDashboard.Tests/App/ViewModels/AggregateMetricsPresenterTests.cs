using FluentAssertions;
using ProductionDashboard.Core.Production;
using ProductionDashboard.ViewModels.Dashboard;

namespace ProductionDashboard.Tests.App.ViewModels;

/// <summary>
/// AggregateMetricsPresenter 的纯计算函数测试(mini 柱图分桶、累计对比、领先判定)。
/// 这些函数是 internal static 的纯函数,不依赖 UI 线程,可以直接测试。
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

    // ── 累计对比行:领先标签与颜色 ────────────────────────────────

    [Fact]
    public void BuildCumulativeRow_marks_team_a_as_leader_when_a_is_ahead()
    {
        var row = AggregateMetricsPresenter.BuildCumulativeRow("本年", 396_411, 306_096, "A 班", "B 班");

        row.DiffText.Should().Be("A 班 +90,315");
        row.LeaderKind.Should().Be("A");
        row.APct.Should().Be(100);
    }

    [Fact]
    public void BuildCumulativeRow_marks_team_b_as_leader_when_b_is_ahead()
    {
        var row = AggregateMetricsPresenter.BuildCumulativeRow("本周", 7_904, 9_851, "A 班", "B 班");

        row.DiffText.Should().Be("B 班 +1,947");
        row.LeaderKind.Should().Be("B", "B 班领先时标签应使用 B 班的颜色");
        row.BPct.Should().Be(100);
    }

    [Fact]
    public void BuildCumulativeRow_marks_a_tie_as_neutral()
    {
        var row = AggregateMetricsPresenter.BuildCumulativeRow("本月", 1_200, 1_200, "A 班", "B 班");

        row.DiffText.Should().Be("持平");
        row.LeaderKind.Should().Be("T");
    }

    [Theory]
    [InlineData(15_661, 12_255, "A")]
    [InlineData(12_255, 15_661, "B")]
    [InlineData(500, 500, "T")]
    [InlineData(0, 0, "T")]
    public void ComputeLeaderKind_picks_the_team_with_more_output(int a, int b, string expected)
    {
        AggregateMetricsPresenter.ComputeLeaderKind(a, b).Should().Be(expected);
    }
}
