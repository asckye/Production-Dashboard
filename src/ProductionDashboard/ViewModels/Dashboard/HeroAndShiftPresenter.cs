using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Core.Statistics;

namespace ProductionDashboard.ViewModels.Dashboard;

/// <summary>
/// Dashboard 的 Hero 大数字卡 + 白班/夜班对比卡 presenter。
/// <list type="bullet">
///   <item>Hero(<c>HeroCount/Target/Percent/Takt</c>):事件驱动,跟当前活跃班组实时累计</item>
///   <item>A/B 卡(<c>A*/B*</c>):2 秒周期查询(由 facade 触发 <see cref="RefreshFromAnalyticsAsync"/>)+ 班次切换 / 倒班即时刷新</item>
/// </list>
/// </summary>
public sealed partial class HeroAndShiftPresenter : ObservableObject, IDisposable
{
    private readonly IShiftManager _shifts;
    private readonly ICountingService _counter;
    private readonly ITeamAssignmentService _teamAssign;
    private readonly IProductionStatisticsService _statistics;
    private readonly Dispatcher _ui;
    private bool _disposed;

    /// <summary>注入班次 / 计数 / 班组 / 分析仓库 / UI dispatcher,订阅事件并初始化 Hero + A/B 卡。</summary>
    public HeroAndShiftPresenter(
        IShiftManager shifts,
        ICountingService counter,
        ITeamAssignmentService teamAssign,
        IProductionStatisticsService statistics,
        Dispatcher uiDispatcher)
    {
        _shifts = shifts;
        _counter = counter;
        _teamAssign = teamAssign;
        _statistics = statistics;
        _ui = uiDispatcher;

        _counter.TeamCountChanged += OnTeamCountChanged;
        _shifts.ShiftChanged += OnShiftChanged;
        _shifts.DefinitionsChanged += OnDefinitionsChanged;
        _teamAssign.AssignmentsChanged += OnAssignmentsChanged;

        // 启动期初始化(同步执行,不要 InvokeAsync — 构造期还没进入 UI 消息循环)
        UpdateShiftLabels(_shifts.Current);
        UpdateSlotTeamLabels();
        RefreshHeroFromCurrentTeam();
        UpdateShiftTimes();
    }

    /// <summary>取消订阅所有事件,幂等。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _counter.TeamCountChanged -= OnTeamCountChanged;
        _shifts.ShiftChanged -= OnShiftChanged;
        _shifts.DefinitionsChanged -= OnDefinitionsChanged;
        _teamAssign.AssignmentsChanged -= OnAssignmentsChanged;
    }

    // ── ObservableProperties ────────────────────────────────────────

    // Hero (当前班次)
    /// <summary>Hero 大数字 — 当前活跃班次的实时累计(N0)。</summary>
    [ObservableProperty] public partial string HeroCount { get; set; } = "0";
    /// <summary>Hero 目标 — 当前班次的 TargetCount(N0)。</summary>
    [ObservableProperty] public partial string HeroTarget { get; set; } = "0";
    /// <summary>Hero 完成度百分比(0-100,上限截断)。</summary>
    [ObservableProperty] public partial double HeroPercent { get; set; }
    /// <summary>Hero 完成度文本 "0.0%"。</summary>
    [ObservableProperty] public partial string HeroPercentText { get; set; } = "0%";
    /// <summary>节拍 — 最近 5 分钟事件 / 5(件/分),由 2 秒 timer 触发刷新。</summary>
    [ObservableProperty] public partial string HeroTakt { get; set; } = "—";

    // 班组对比卡 — A
    /// <summary>A 槽时间段文本(例 "08:00 - 20:00" 或 "20:00 - 次日 08:00")。</summary>
    [ObservableProperty] public partial string ATime { get; set; } = "—";
    /// <summary>A 槽状态文本("进行中" / "已结束" / "未开始")。</summary>
    [ObservableProperty] public partial string AStatus { get; set; } = "未开始";
    /// <summary>A 槽是否为当前活跃班次。UI 据此切换高亮。</summary>
    [ObservableProperty] public partial bool AIsActive { get; set; }
    /// <summary>A 槽今日累计(N0;0 时显示 "—")。</summary>
    [ObservableProperty] public partial string ACount { get; set; } = "—";
    /// <summary>A 槽目标产量(从 ShiftDefinition.TargetCount)。</summary>
    [ObservableProperty] public partial string ATarget { get; set; } = "0";
    /// <summary>A 槽完成度百分比。</summary>
    [ObservableProperty] public partial double APercent { get; set; }
    /// <summary>A 槽完成度文本。</summary>
    [ObservableProperty] public partial string APercentText { get; set; } = "0%";
    /// <summary>A 槽当前分配到的班组名(例 "A 班" / "未分配")。</summary>
    [ObservableProperty] public partial string ATeamLabel { get; set; } = "A 班";
    /// <summary>A 槽班长行 "班长:张三"。</summary>
    [ObservableProperty] public partial string ATeamLeader { get; set; } = "";

    // 班组对比卡 — B
    /// <summary>B 槽时间段文本。</summary>
    [ObservableProperty] public partial string BTime { get; set; } = "—";
    /// <summary>B 槽状态文本。</summary>
    [ObservableProperty] public partial string BStatus { get; set; } = "未开始";
    /// <summary>B 槽是否为当前活跃班次。</summary>
    [ObservableProperty] public partial bool BIsActive { get; set; }
    /// <summary>B 槽今日累计。</summary>
    [ObservableProperty] public partial string BCount { get; set; } = "—";
    /// <summary>B 槽目标产量。</summary>
    [ObservableProperty] public partial string BTarget { get; set; } = "0";
    /// <summary>B 槽完成度百分比。</summary>
    [ObservableProperty] public partial double BPercent { get; set; }
    /// <summary>B 槽完成度文本。</summary>
    [ObservableProperty] public partial string BPercentText { get; set; } = "0%";
    /// <summary>B 槽当前分配到的班组名。</summary>
    [ObservableProperty] public partial string BTeamLabel { get; set; } = "B 班";
    /// <summary>B 槽班长行。</summary>
    [ObservableProperty] public partial string BTeamLeader { get; set; } = "";

    // ── 事件 → UI 推送 ─────────────────────────────────────────────

    private void OnTeamCountChanged(int teamId, int newCount)
    {
        _ui.InvokeAsync(() =>
        {
            var current = _shifts.Current;
            if (current is null) return;
            var slotTeam = _teamAssign.GetTeamForSlot(current.Definition.Id);
            if (slotTeam is null) return;
            if (slotTeam.Id != teamId) return;
            UpdateHeroForCurrentTeamCount(current, newCount);
        });
    }

    private void OnShiftChanged(ShiftAssignment? oldShift, ShiftAssignment? newShift)
    {
        _ui.InvokeAsync(() =>
        {
            UpdateShiftLabels(newShift);
            UpdateSlotTeamLabels();
            UpdateShiftTimes();
            RefreshHeroFromCurrentTeam();
        });
    }

    private void OnAssignmentsChanged()
    {
        _ui.InvokeAsync(() =>
        {
            UpdateSlotTeamLabels();
            RefreshHeroFromCurrentTeam();
        });
    }

    private void OnDefinitionsChanged()
    {
        _ui.InvokeAsync(() =>
        {
            UpdateShiftTimes();
            UpdateSlotTeamLabels();
            RefreshHeroFromCurrentTeam();
        });
    }

    // ── 内部状态推算 ───────────────────────────────────────────────

    private void RefreshHeroFromCurrentTeam()
    {
        var current = _shifts.Current;
        if (current is null)
        {
            HeroCount = "0";
            HeroPercent = 0;
            HeroPercentText = "0%";
            return;
        }
        var slotTeam = _teamAssign.GetTeamForSlot(current.Definition.Id);
        var count = slotTeam is null ? 0 : _counter.GetCountForTeam(slotTeam.Id);
        UpdateHeroForCurrentTeamCount(current, count);
    }

    private void UpdateHeroForCurrentTeamCount(ShiftAssignment current, int newCount)
    {
        HeroCount = newCount.ToString("N0");
        HeroTarget = current.Definition.TargetCount.ToString("N0");
        var pct = current.Definition.TargetCount == 0
            ? 0
            : Math.Min(100.0, newCount * 100.0 / current.Definition.TargetCount);
        HeroPercent = pct;
        HeroPercentText = $"{pct:F1}%";
    }

    private void UpdateShiftLabels(ShiftAssignment? current)
    {
        var (aDef, bDef) = ComparedShifts.Pick(_shifts.Definitions);
        AIsActive = current is not null && current.Definition.Id == aDef?.Id;
        BIsActive = current is not null && current.Definition.Id == bDef?.Id;

        Debug.Assert(!(AIsActive && BIsActive), "AIsActive 与 BIsActive 必须互斥");

        // 按班次时间窗口径,非活跃班次的卡片显示"最近一次完成的"周期总量,因此状态显示"已结束";
        // 只有 current=null(冷启动且没有班次定义)时显示"未开始"。
        AStatus = AIsActive ? "进行中" : (current is null ? "未开始" : "已结束");
        BStatus = BIsActive ? "进行中" : (current is null ? "未开始" : "已结束");
    }

    private void UpdateShiftTimes()
    {
        var (aDef, bDef) = ComparedShifts.Pick(_shifts.Definitions);

        ATime = aDef is null
            ? "—"
            : (aDef.CrossesMidnight
                ? $"{aDef.StartTime} - 次日 {aDef.EndTime}"
                : $"{aDef.StartTime} - {aDef.EndTime}");

        BTime = bDef is null
            ? "—"
            : (bDef.CrossesMidnight
                ? $"{bDef.StartTime} - 次日 {bDef.EndTime}"
                : $"{bDef.StartTime} - {bDef.EndTime}");
    }

    private void UpdateSlotTeamLabels()
    {
        var (aDef, bDef) = ComparedShifts.Pick(_shifts.Definitions);

        var aTeam = aDef is null ? null : _teamAssign.GetTeamForSlot(aDef.Id);
        var bTeam = bDef is null ? null : _teamAssign.GetTeamForSlot(bDef.Id);

        ATeamLabel = aTeam is null ? "未分配" : aTeam.Name;
        ATeamLeader = aTeam is null ? "" : $"班长:{aTeam.LeaderName}";
        BTeamLabel = bTeam is null ? "未分配" : bTeam.Name;
        BTeamLeader = bTeam is null ? "" : $"班长:{bTeam.LeaderName}";
    }

    // ── 周期性聚合(由 facade 2s timer 触发) ─────────────────────

    /// <summary>
    /// 从统计服务刷新 A/B 卡 + Hero 节拍。
    /// <para>
    /// <b>口径:全按班次时间窗</b>。A 卡显示"最近一次或正在进行的白班"产量;
    /// B 卡同理对夜班。每次班次开始时归零,班次结束后保持终态,直到下一次同名班次开始。
    /// 跟 Hero 大数字的"班次切片"语义对齐 — 三张卡在班次边界同步重置。
    /// </para>
    /// <para>
    /// 副作用:卡片数字不再有 <c>ACount + BCount = DayCount</c> 的数学恒等,因为
    /// "今日 ShiftDate"和"班次实际时间窗"是不同维度;但今日卡(5 维度)仍按
    /// ShiftDate 统计,不受此改动影响。
    /// </para>
    /// </summary>
    public async Task RefreshFromAnalyticsAsync(CancellationToken ct)
    {
        var stats = await _statistics.GetShiftCardStatisticsAsync(ct).ConfigureAwait(false);
        var takt = stats.PartsPerMinute;

        await _ui.InvokeAsync(() =>
        {
            ApplyAbCards(stats.DayShift?.Definition, stats.NightShift?.Definition,
                stats.DayShift?.Count, stats.NightShift?.Count);
            HeroTakt = takt >= 1 ? takt.ToString("F0") : takt.ToString("F1");
        });
    }

    private void ApplyAbCards(
        ShiftDefinition? aDef, ShiftDefinition? bDef, int? aTodayCount, int? bTodayCount)
    {
        if (aDef is not null && aTodayCount.HasValue)
        {
            ACount = aTodayCount.Value == 0 ? "—" : aTodayCount.Value.ToString("N0");
            ATarget = aDef.TargetCount.ToString("N0");
            APercent = aDef.TargetCount == 0
                ? 0
                : Math.Min(100.0, aTodayCount.Value * 100.0 / aDef.TargetCount);
            APercentText = $"{APercent:F1}%";
        }
        if (bDef is not null && bTodayCount.HasValue)
        {
            BCount = bTodayCount.Value == 0 ? "—" : bTodayCount.Value.ToString("N0");
            BTarget = bDef.TargetCount.ToString("N0");
            BPercent = bDef.TargetCount == 0
                ? 0
                : Math.Min(100.0, bTodayCount.Value * 100.0 / bDef.TargetCount);
            BPercentText = $"{BPercent:F1}%";
        }
    }

}
