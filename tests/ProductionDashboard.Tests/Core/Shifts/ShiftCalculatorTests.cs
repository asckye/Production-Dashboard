using FluentAssertions;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Tests.Core.Shifts;

/// <summary>
/// 班次判定的核心逻辑测试。重点:跨 0 点 / 边界时刻 / 归属规则 / N 班通用。
/// </summary>
public sealed class ShiftCalculatorTests
{
    // ────────────────────────────────────────────────────────────
    //  Helpers — 工厂方法 + 预置班次集
    // ────────────────────────────────────────────────────────────

    /// <summary>快速构造一条 <see cref="ShiftDefinition"/>,默认目标 1000 / 白色,
    /// 使测试用例只关注关心的字段(代码 / 起止 / 是否跨日)。</summary>
    private static ShiftDefinition Make(
        int id, string code, string name, string start, string end,
        bool crossesMidnight, int target = 1000, string color = "#fff")
        => new(
            Id: id, Code: code, Name: name,
            StartTime: TimeOnly.ParseExact(start, "HH:mm"), EndTime: TimeOnly.ParseExact(end, "HH:mm"),
            CrossesMidnight: crossesMidnight,
            TargetCount: target, ColorHex: color, DisplayOrder: id,
            EffectiveFrom: new DateOnly(1970, 1, 1),
            EffectiveTo: null,
            CreatedAt: DateTime.UtcNow);

    private static readonly IReadOnlyList<ShiftDefinition> TwoShifts =
    [
        Make(1, "A", "白班", "08:00", "20:00", crossesMidnight: false),
        Make(2, "B", "夜班", "20:00", "08:00", crossesMidnight: true),
    ];

    private static readonly IReadOnlyList<ShiftDefinition> ThreeShifts =
    [
        Make(1, "Early", "早班", "08:00", "16:00", crossesMidnight: false),
        Make(2, "Mid",   "中班", "16:00", "00:00", crossesMidnight: true),
        Make(3, "Night", "晚班", "00:00", "08:00", crossesMidnight: false),
    ];

    // ────────────────────────────────────────────────────────────
    //  空 / null 情况
    // ────────────────────────────────────────────────────────────

    /// <summary>定义集合为空时 Compute 应返回 null,代表"无活动班次"。</summary>
    [Fact]
    public void Empty_definitions_returns_null()
    {
        var result = ShiftCalculator.Compute(
            Array.Empty<ShiftDefinition>(),
            new DateTime(2026, 5, 19, 14, 0, 0),
            ShiftAttributionRule.StartDate);

        result.Should().BeNull();
    }

    // ────────────────────────────────────────────────────────────
    //  两班倒:边界全覆盖
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 两班倒(A 08:00-20:00 / B 20:00-08:00)在同一天内的边界覆盖测试,
    /// 重点验证开始/结束精确时刻的归属(start ≤ now &lt; end)。
    /// </summary>
    [Theory]
    [InlineData(8, 0, 0, "A")]      // 白班开始那一刻
    [InlineData(8, 0, 1, "A")]      // 紧贴开始
    [InlineData(14, 23, 8, "A")]    // 白班中段
    [InlineData(19, 59, 59, "A")]   // 白班即将结束
    [InlineData(20, 0, 0, "B")]     // 夜班开始那一刻 — 关键边界
    [InlineData(20, 0, 1, "B")]
    [InlineData(23, 59, 59, "B")]   // 接近 0 点
    public void TwoShifts_day19(int hour, int min, int sec, string expectedCode)
    {
        var t = new DateTime(2026, 5, 19, hour, min, sec);

        var result = ShiftCalculator.Compute(
            TwoShifts, t, ShiftAttributionRule.StartDate);

        result.Should().NotBeNull();
        result!.Definition.Code.Should().Be(expectedCode);
        result.ShiftDate.Should().Be(new DateOnly(2026, 5, 19));
    }

    /// <summary>
    /// StartDate 归属规则下,凌晨 00:00-08:00 应归属"昨晚开始的夜班",
    /// ShiftDate 也应记为前一天。
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(3, 30, 0)]
    [InlineData(7, 59, 59)]
    public void Crossover_morning_belongs_to_previous_night_shift_with_StartDate_rule(
        int hour, int min, int sec)
    {
        // 凌晨 0:00-08:00 属于昨晚开始的夜班
        var t = new DateTime(2026, 5, 20, hour, min, sec);

        var result = ShiftCalculator.Compute(
            TwoShifts, t, ShiftAttributionRule.StartDate);

        result.Should().NotBeNull();
        result!.Definition.Code.Should().Be("B");
        result.ShiftDate.Should().Be(new DateOnly(2026, 5, 19), "夜班归属开始日");
        result.PeriodStart.Should().Be(new DateTime(2026, 5, 19, 20, 0, 0));
        result.PeriodEnd.Should().Be(new DateTime(2026, 5, 20, 8, 0, 0));
    }

    /// <summary>EndDate 归属规则下,凌晨那段应归属"当天",即跨日班次算到结束日。</summary>
    [Fact]
    public void Crossover_morning_belongs_to_today_with_EndDate_rule()
    {
        var t = new DateTime(2026, 5, 20, 3, 30, 0);

        var result = ShiftCalculator.Compute(
            TwoShifts, t, ShiftAttributionRule.EndDate);

        result.Should().NotBeNull();
        result!.Definition.Code.Should().Be("B");
        result.ShiftDate.Should().Be(new DateOnly(2026, 5, 20), "EndDate 规则下夜班算结束日");
    }

    /// <summary>StartDate 规则:夜班晚上 21:00 应归"当晚",PeriodEnd 跨到明早 08:00。</summary>
    [Fact]
    public void Night_shift_in_evening_uses_start_day_with_StartDate_rule()
    {
        // 5月19日 21:00 — 夜班开始那天就是 5月19日
        var t = new DateTime(2026, 5, 19, 21, 0, 0);
        var r = ShiftCalculator.Compute(TwoShifts, t, ShiftAttributionRule.StartDate);

        r!.Definition.Code.Should().Be("B");
        r.ShiftDate.Should().Be(new DateOnly(2026, 5, 19));
        r.PeriodEnd.Should().Be(new DateTime(2026, 5, 20, 8, 0, 0));
    }

    /// <summary>EndDate 规则:夜班晚上 21:00 应归"明日",ShiftDate = 明日。</summary>
    [Fact]
    public void Night_shift_in_evening_uses_end_day_with_EndDate_rule()
    {
        var t = new DateTime(2026, 5, 19, 21, 0, 0);
        var r = ShiftCalculator.Compute(TwoShifts, t, ShiftAttributionRule.EndDate);

        r!.Definition.Code.Should().Be("B");
        r.ShiftDate.Should().Be(new DateOnly(2026, 5, 20), "EndDate 规则下算到结束日");
    }

    /// <summary>边界翻越:08:00:00 整点应归白班(A),夜班(B)在 08:00 严格小于,已结束。</summary>
    [Fact]
    public void Boundary_exact_8am_transitions_from_night_to_day_shift()
    {
        // 5月20日 08:00:00 是 A 班开始的瞬间,B 班已经结束(end 是 < 比较)
        var t = new DateTime(2026, 5, 20, 8, 0, 0);
        var r = ShiftCalculator.Compute(TwoShifts, t, ShiftAttributionRule.StartDate);

        r!.Definition.Code.Should().Be("A");
        r.ShiftDate.Should().Be(new DateOnly(2026, 5, 20));
    }

    // ────────────────────────────────────────────────────────────
    //  三班倒:8h × 3
    // ────────────────────────────────────────────────────────────

    /// <summary>三班倒(8h × 3)应严丝合缝覆盖 24 小时,任意时刻都能定位到唯一班次。</summary>
    [Theory]
    [InlineData(0, 0, 0, "Night")]    // 晚班 00:00-08:00
    [InlineData(7, 59, 59, "Night")]
    [InlineData(8, 0, 0, "Early")]    // 早班 08:00-16:00
    [InlineData(15, 59, 59, "Early")]
    [InlineData(16, 0, 0, "Mid")]     // 中班 16:00-00:00
    [InlineData(23, 59, 59, "Mid")]
    public void ThreeShifts_cover_24h(int hour, int min, int sec, string code)
    {
        var t = new DateTime(2026, 5, 19, hour, min, sec);
        var r = ShiftCalculator.Compute(
            ThreeShifts, t, ShiftAttributionRule.StartDate);

        r.Should().NotBeNull();
        r!.Definition.Code.Should().Be(code);
    }

    /// <summary>
    /// 中班 16:00-00:00 跨子夜结束时:
    /// 5月20日 00:01 应归属当晚的 Night,而不是已经结束的 Mid。
    /// </summary>
    [Fact]
    public void ThreeShifts_mid_shift_crosses_midnight_attribution()
    {
        // 5月20日 凌晨 0:30,中班 16:00 开始,跨 0 点到 00:00 — 这里中班 endTime 正好是 "00:00"
        // 实际上 16:00-00:00 这种 endTime=00:00 表示子夜,CrossesMidnight=true
        // 因此 5月20日 00:01 不应该归属中班(中班已经结束),应归 Night
        var lateNight = new DateTime(2026, 5, 20, 0, 1, 0);
        var r = ShiftCalculator.Compute(
            ThreeShifts, lateNight, ShiftAttributionRule.StartDate);

        r!.Definition.Code.Should().Be("Night");
        r.ShiftDate.Should().Be(new DateOnly(2026, 5, 20));
    }

    // ────────────────────────────────────────────────────────────
    //  单班制(只有白天有产量,夜间空档)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 单班制(白班 08:00-17:00):工作时段内返回该班次;
    /// 工作时段外(夜晚 / 早晨)应返回 null,代表"无班次空档"。
    /// </summary>
    [Fact]
    public void Single_shift_returns_null_outside_window()
    {
        IReadOnlyList<ShiftDefinition> single =
        [
            Make(1, "Day", "白班", "08:00", "17:00", crossesMidnight: false),
        ];

        var inside = ShiftCalculator.Compute(
            single, new DateTime(2026, 5, 19, 12, 0, 0), ShiftAttributionRule.StartDate);
        inside!.Definition.Code.Should().Be("Day");

        var outside = ShiftCalculator.Compute(
            single, new DateTime(2026, 5, 19, 18, 0, 0), ShiftAttributionRule.StartDate);
        outside.Should().BeNull("17:00 之后没有任何班次");
    }

    // ────────────────────────────────────────────────────────────
    //  PeriodStart / PeriodEnd 计算
    // ────────────────────────────────────────────────────────────

    /// <summary>同日班次:PeriodStart / PeriodEnd 都应落在同一日历日内。</summary>
    [Fact]
    public void Same_day_period_matches_start_and_end_times()
    {
        var t = new DateTime(2026, 5, 19, 10, 0, 0);
        var r = ShiftCalculator.Compute(TwoShifts, t, ShiftAttributionRule.StartDate);

        r!.PeriodStart.Should().Be(new DateTime(2026, 5, 19, 8, 0, 0));
        r.PeriodEnd.Should().Be(new DateTime(2026, 5, 19, 20, 0, 0));
    }

    /// <summary>跨子夜班次:PeriodStart / PeriodEnd 跨两个日历日,总时长仍是 12 小时。</summary>
    [Fact]
    public void Cross_midnight_period_spans_two_calendar_days()
    {
        var t = new DateTime(2026, 5, 19, 23, 0, 0);
        var r = ShiftCalculator.Compute(TwoShifts, t, ShiftAttributionRule.StartDate);

        r!.PeriodStart.Should().Be(new DateTime(2026, 5, 19, 20, 0, 0));
        r.PeriodEnd.Should().Be(new DateTime(2026, 5, 20, 8, 0, 0));
        (r.PeriodEnd - r.PeriodStart).Should().Be(TimeSpan.FromHours(12));
    }

    // ────────────────────────────────────────────────────────────
    //  GetMostRecentPeriod — Dashboard A/B 卡按"班次时段"切片
    // ────────────────────────────────────────────────────────────

    /// <summary>白班进行中,问"最近一次白班" → 返回当前正在进行的(IsActive=true)。</summary>
    [Fact]
    public void GetMostRecentPeriod_dayshift_running_returns_current_active()
    {
        var dayShift = TwoShifts[0];   // 白班 08:00-20:00
        var t = new DateTime(2026, 5, 24, 14, 0, 0);

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(dayShift, t);

        start.Should().Be(new DateTime(2026, 5, 24, 8, 0, 0));
        end.Should().Be(new DateTime(2026, 5, 24, 20, 0, 0));
        isActive.Should().BeTrue();
    }

    /// <summary>夜班期间,问"最近一次白班" → 返回今天 08:00-20:00 那段(刚结束)。</summary>
    [Fact]
    public void GetMostRecentPeriod_dayshift_during_night_returns_today_ended()
    {
        var dayShift = TwoShifts[0];
        var t = new DateTime(2026, 5, 24, 22, 0, 0);

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(dayShift, t);

        start.Should().Be(new DateTime(2026, 5, 24, 8, 0, 0));
        end.Should().Be(new DateTime(2026, 5, 24, 20, 0, 0));
        isActive.Should().BeFalse();
    }

    /// <summary>凌晨(白班还没开始),问"最近一次白班" → 返回昨天 08:00-20:00 那段。</summary>
    [Fact]
    public void GetMostRecentPeriod_dayshift_early_morning_returns_yesterday()
    {
        var dayShift = TwoShifts[0];
        var t = new DateTime(2026, 5, 24, 3, 0, 0);

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(dayShift, t);

        start.Should().Be(new DateTime(2026, 5, 23, 8, 0, 0));
        end.Should().Be(new DateTime(2026, 5, 23, 20, 0, 0));
        isActive.Should().BeFalse();
    }

    /// <summary>白班期间,问"最近一次夜班" → 返回昨晚 20:00 - 今早 08:00 那段(刚结束)。</summary>
    [Fact]
    public void GetMostRecentPeriod_nightshift_during_day_returns_last_night_ended()
    {
        var nightShift = TwoShifts[1];   // 夜班 20:00 -> 08:00
        var t = new DateTime(2026, 5, 24, 14, 0, 0);

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(nightShift, t);

        start.Should().Be(new DateTime(2026, 5, 23, 20, 0, 0));
        end.Should().Be(new DateTime(2026, 5, 24, 8, 0, 0));
        isActive.Should().BeFalse();
    }

    /// <summary>晚 22:00 夜班进行中,问"最近一次夜班" → 今晚 20:00 开始的那段。</summary>
    [Fact]
    public void GetMostRecentPeriod_nightshift_evening_returns_current_active()
    {
        var nightShift = TwoShifts[1];
        var t = new DateTime(2026, 5, 24, 22, 0, 0);

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(nightShift, t);

        start.Should().Be(new DateTime(2026, 5, 24, 20, 0, 0));
        end.Should().Be(new DateTime(2026, 5, 25, 8, 0, 0));
        isActive.Should().BeTrue();
    }

    /// <summary>凌晨 02:00 夜班进行中(跨过午夜),问"最近一次夜班" → 昨晚 20:00 起的那段,仍在进行。</summary>
    [Fact]
    public void GetMostRecentPeriod_nightshift_past_midnight_returns_active_overnight()
    {
        var nightShift = TwoShifts[1];
        var t = new DateTime(2026, 5, 25, 2, 0, 0);

        var (start, end, isActive) = ShiftCalculator.GetMostRecentPeriod(nightShift, t);

        start.Should().Be(new DateTime(2026, 5, 24, 20, 0, 0));
        end.Should().Be(new DateTime(2026, 5, 25, 8, 0, 0));
        isActive.Should().BeTrue();
    }

    /// <summary>边界:夜班结束瞬间 08:00:00 应判定为已结束(end 是开区间)。</summary>
    [Fact]
    public void GetMostRecentPeriod_at_exact_end_boundary_treated_as_ended()
    {
        var nightShift = TwoShifts[1];
        var t = new DateTime(2026, 5, 25, 8, 0, 0);

        var (_, _, isActive) = ShiftCalculator.GetMostRecentPeriod(nightShift, t);

        // 此时刚跨入"白班"刚开始的瞬间,夜班视为已结束
        isActive.Should().BeFalse();
    }
}
