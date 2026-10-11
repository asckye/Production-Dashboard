using FluentAssertions;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.ViewModels.ShiftConfig;

namespace ProductionDashboard.Tests.App.ViewModels;

/// <summary>
/// 班次定义编辑器里的纯函数 helper 测试。
/// <para>ComputeDurationMinutes 是覆盖检测和 EditableShift.DurationLabel 共用的核心算法,
/// 跨 0 点边界对班次配置影响巨大,值得固化为可回归的契约。</para>
/// </summary>
public sealed class ShiftDefinitionEditorHelpersTests
{
    [Theory]
    [InlineData(8, 0, 20, 0, 12 * 60)]       // 同日 12 小时
    [InlineData(8, 0, 17, 0, 9 * 60)]        // 同日 9 小时
    [InlineData(0, 0, 8, 0, 8 * 60)]         // 早班 0:00-8:00
    [InlineData(20, 0, 8, 0, 12 * 60)]       // 跨日 20:00 → 次日 8:00 = 12 小时
    [InlineData(23, 30, 0, 30, 60)]          // 极短跨日 23:30 → 0:30 = 1 小时
    [InlineData(0, 0, 0, 0, 24 * 60)]        // 0:00 → 0:00 视为整 24 小时(跨日逻辑)
    [InlineData(8, 0, 8, 0, 24 * 60)]        // 同时刻 = 整 24 小时(跨日逻辑)
    [InlineData(6, 15, 14, 45, 8 * 60 + 30)] // 不整点 6:15 → 14:45 = 8 小时 30 分
    public void ComputeDurationMinutes_returns_expected_for_typical_cases(
        int sh, int sm, int eh, int em, int expectedMinutes)
    {
        var start = new TimeOnly(sh, sm);
        var end = new TimeOnly(eh, em);

        var actual = ShiftDefinitionEditor.ComputeDurationMinutes(start, end);

        actual.Should().Be(expectedMinutes);
    }

    [Fact]
    public void EditableShift_CrossesMidnight_true_when_start_ge_end()
    {
        var es = new EditableShift { StartTime = "20:00", EndTime = "08:00" };
        es.CrossesMidnight.Should().BeTrue();
    }

    [Fact]
    public void EditableShift_CrossesMidnight_false_for_same_day_shift()
    {
        var es = new EditableShift { StartTime = "08:00", EndTime = "17:00" };
        es.CrossesMidnight.Should().BeFalse();
    }

    [Fact]
    public void EditableShift_CrossesMidnight_false_when_time_format_invalid()
    {
        var es = new EditableShift { StartTime = "noon", EndTime = "08:00" };
        es.CrossesMidnight.Should().BeFalse();
    }

    [Theory]
    [InlineData("08:00", "20:00", "12 小时 0 分")]
    [InlineData("20:00", "08:00", "12 小时 0 分")]
    [InlineData("06:15", "14:45", "8 小时 30 分")]
    [InlineData("23:30", "00:30", "1 小时 0 分")]
    public void EditableShift_DurationLabel_formats_correctly(string start, string end, string expected)
    {
        var es = new EditableShift { StartTime = start, EndTime = end };
        es.DurationLabel.Should().Be(expected);
    }

    [Fact]
    public void EditableShift_DurationLabel_returns_dashes_for_invalid_time()
    {
        var es = new EditableShift { StartTime = "garbage", EndTime = "08:00" };
        es.DurationLabel.Should().Be("--");
    }

    /// <summary>EditableShift.From 应该把 ShiftDefinition 字段忠实拷贝到可编辑视图。</summary>
    [Fact]
    public void EditableShift_From_copies_all_persisted_fields()
    {
        var def = new ShiftDefinition(
            42, "C", "晚班", new TimeOnly(16, 0), new TimeOnly(0, 0), true, 800, "#FF0000", 3,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));

        var es = EditableShift.From(def);

        es.Id.Should().Be(42);
        es.Code.Should().Be("C");
        es.Name.Should().Be("晚班");
        es.StartTime.Should().Be("16:00");
        es.EndTime.Should().Be("00:00");
        es.TargetCount.Should().Be(800);
        es.ColorHex.Should().Be("#FF0000");
        es.DisplayOrder.Should().Be(3);
        es.EffectiveFrom.Should().Be(new DateOnly(2026, 1, 1));
        es.EffectiveTo.Should().Be(new DateOnly(2026, 12, 31));
        es.CreatedAt.Should().Be(def.CreatedAt);
        es.IsNew.Should().BeFalse();
        es.IsDeleted.Should().BeFalse();
        es.IsDirty.Should().BeFalse();
    }

    /// <summary>EditableTeam.From 应该把 Team 字段忠实拷贝并保留 OriginalSnapshot 给 Save 用。</summary>
    [Fact]
    public void EditableTeam_From_copies_fields_and_keeps_original_snapshot()
    {
        var team = new Team(
            5, "A 班", "张三", "李四,王五", "#FF8C42", 1, true, DateTime.UtcNow);

        var et = EditableTeam.From(team);

        et.Id.Should().Be(5);
        et.Name.Should().Be("A 班");
        et.LeaderName.Should().Be("张三");
        et.ColorHex.Should().Be("#FF8C42");
        et.DisplayOrder.Should().Be(1);
        et.OriginalSnapshot.Should().BeSameAs(team);
        et.IsDirty.Should().BeFalse();
    }

    /// <summary>编辑 EditableShift 的 StartTime / EndTime 应触发 CrossesMidnight / DurationLabel 重新通知。</summary>
    [Fact]
    public void EditableShift_time_change_notifies_derived_properties()
    {
        var es = new EditableShift { StartTime = "08:00", EndTime = "17:00" };

        var crossesNotified = false;
        var durationNotified = false;
        es.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditableShift.CrossesMidnight)) crossesNotified = true;
            if (e.PropertyName == nameof(EditableShift.DurationLabel)) durationNotified = true;
        };

        es.StartTime = "20:00";

        crossesNotified.Should().BeTrue();
        durationNotified.Should().BeTrue();
        es.CrossesMidnight.Should().BeTrue();
    }
}
