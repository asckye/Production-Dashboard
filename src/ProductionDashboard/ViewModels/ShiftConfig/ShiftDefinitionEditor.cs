using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Services.UI;

namespace ProductionDashboard.ViewModels.ShiftConfig;

/// <summary>
/// 班次定义子编辑器:负责
/// <list type="bullet">
///   <item>班次定义集合(<see cref="Editables"/>)的增删改</item>
///   <item>24h 时间轴片段构造(<see cref="TimelineSegments"/>)</item>
///   <item>覆盖检测(<see cref="CoverageHint"/> / <see cref="IsCoverageValid"/>)</item>
///   <item>快速模板应用(单班/两班/三班/四班)</item>
///   <item>NOW 红线 + 时间标签的 1 秒刷新</item>
/// </list>
/// </summary>
public sealed partial class ShiftDefinitionEditor : ObservableObject
{
    private readonly IShiftConfigurationService _configuration;
    private readonly IShiftManager _shifts;
    private readonly IToastService _toast;
    private readonly IAuthService _auth;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _nowMarkerTimer;

    /// <summary>构造并启动 1 秒 NOW 红线刷新 timer。dispatcher 用于把 UI 推送派发回主线程。</summary>
    public ShiftDefinitionEditor(
        IShiftConfigurationService configuration,
        IShiftManager shifts,
        IToastService toast,
        IAuthService auth,
        TimeProvider clock,
        Dispatcher dispatcher)
    {
        _configuration = configuration;
        _shifts = shifts;
        _toast = toast;
        _auth = auth;
        _clock = clock;

        AddShiftCommand = new RelayCommand(AddShift);
        DeleteShiftCommand = new RelayCommand<EditableShift>(DeleteShift, es => es is not null);
        ApplyTemplateCommand = new RelayCommand<ShiftTemplate>(ApplyTemplate, t => t is not null);

        Editables = new ObservableCollection<EditableShift>();
        TimelineSegments = new ObservableCollection<TimelineSegment>();
        Templates = BuildBuiltInTemplates();
        EffectiveDateText = $"{LocalNow:yyyy-MM-dd}";

        _nowMarkerTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => RefreshNowMarker(),
            dispatcher);
        _nowMarkerTimer.Start();
    }

    /// <summary>当前本地时间(通过注入的 <see cref="TimeProvider"/> 读取,便于测试)。</summary>
    private DateTime LocalNow => _clock.GetLocalNow().DateTime;

    /// <summary>双向绑定的班次定义列表(增 / 删 / 改都直接在这里操作)。</summary>
    public ObservableCollection<EditableShift> Editables { get; }

    /// <summary>24 小时时间轴片段集合,UI 直接渲染(LeftPercent + WidthPercent 定位)。</summary>
    public ObservableCollection<TimelineSegment> TimelineSegments { get; }

    /// <summary>「单班 / 两班倒 / 三班倒 / 四班三运转」内置模板,一键应用替换当前班次集。</summary>
    public IReadOnlyList<ShiftTemplate> Templates { get; }

    /// <summary>覆盖检测的人类可读提示("覆盖完整 24 小时" / "空缺 X 小时" / "重叠 X 分")。</summary>
    [ObservableProperty] public partial string CoverageHint { get; set; } = "";

    /// <summary>覆盖是否合法(true = 整 24 小时无空档无重叠)。Save 按钮可根据此禁用。</summary>
    [ObservableProperty] public partial bool IsCoverageValid { get; set; }

    /// <summary>NOW 红线在时间轴上的左偏(0-100 百分比)。每秒由 timer 刷新。</summary>
    [ObservableProperty] public partial double NowMarkerLeftPercent { get; set; }

    /// <summary>NOW 红线旁边的时间标签,"NOW HH:mm:ss"。每秒刷新让秒数可见跳动。</summary>
    [ObservableProperty] public partial string NowMarkerTimeText { get; set; } = "";

    /// <summary>"共 N 个班次 · 总目标 X 件/天"摘要文本。</summary>
    [ObservableProperty] public partial string CoverageSummary { get; set; } = "";

    /// <summary>生效日期文本(目前显示今天)。后续若做版本化班次配置可改 init 来源。</summary>
    [ObservableProperty] public partial string EffectiveDateText { get; set; } = "";

    /// <summary>新增一个班次(默认占下一个可用 Code)。</summary>
    public IRelayCommand AddShiftCommand { get; }

    /// <summary>删除一个班次(已持久化的走 SoftDelete,未持久化的直接移除)。</summary>
    public IRelayCommand<EditableShift> DeleteShiftCommand { get; }

    /// <summary>应用快速模板(替换当前所有班次)。</summary>
    public IRelayCommand<ShiftTemplate> ApplyTemplateCommand { get; }

    /// <summary>有 dirty / deleted 班次时触发,facade 据此聚合 HasUnsavedChanges。</summary>
    public event Action? DirtyStateChanged;

    /// <summary>是否有未保存改动(任一 dirty 或 deleted)。facade 据此聚合 Save 按钮的 CanExecute。</summary>
    public bool HasUnsavedChanges => Editables.Any(s => s.IsDirty || s.IsDeleted);

    /// <summary>当前 dirty + deleted 的班次数量,用于显示 toast 摘要 / 调试。</summary>
    public int DirtyCount => Editables.Count(s => s.IsDirty || s.IsDeleted);

    /// <summary>从 ShiftManager 重新加载班次定义到 Editables(放弃任何未保存改动)。</summary>
    public void Reload()
    {
        foreach (var e in Editables) e.PropertyChanged -= OnEditablePropertyChanged;
        Editables.Clear();
        foreach (var def in _shifts.Definitions.OrderBy(s => s.DisplayOrder))
        {
            var editable = EditableShift.From(def);
            editable.PropertyChanged += OnEditablePropertyChanged;
            Editables.Add(editable);
        }
        UpdateCoverageHint();
        RebuildTimeline();
    }

    /// <summary>Save 成功后调用,把所有 Editable 的 IsDirty 置回 false。</summary>
    public void ClearDirty()
    {
        foreach (var e in Editables) e.IsDirty = false;
    }

    /// <summary>停止内部 NOW 红线 timer。facade Dispose 时调用,避免泄露 DispatcherTimer。</summary>
    public void StopTimers() => _nowMarkerTimer.Stop();

    // ── 新增 / 删除 ────────────────────────────────────────────────

    private void AddShift()
    {
        // 新班次默认占用第一个可用的 Code(C, D, E…)
        var usedCodes = Editables.Select(e => e.Code).ToHashSet();
        var newCode = "";
        for (var c = 'C'; c <= 'Z'; c++)
        {
            if (!usedCodes.Contains(c.ToString()))
            {
                newCode = c.ToString();
                break;
            }
        }
        if (string.IsNullOrEmpty(newCode))
        {
            _toast.ShowWarning("班次编号已用完 (A-Z),请删除部分班次后再新增");
            return;
        }

        // 默认配色按 DisplayOrder 轮换
        var palette = new[] { "#FF8C42", "#4A9EFF", "#00FF88", "#AA55FF", "#FFAA00", "#FF6B9D" };
        var color = palette[Editables.Count % palette.Length];

        var es = new EditableShift
        {
            Id = 0,
            Code = newCode,
            Name = $"班次 {newCode}",
            StartTime = "08:00",
            EndTime = "16:00",
            TargetCount = 1000,
            ColorHex = color,
            DisplayOrder = Editables.Count + 1,
            EffectiveFrom = DateOnly.FromDateTime(LocalNow),
            EffectiveTo = null,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            IsDirty = true,
            IsNew = true,
        };
        es.PropertyChanged += OnEditablePropertyChanged;
        Editables.Add(es);
        UpdateCoverageHint();
        RebuildTimeline();
        DirtyStateChanged?.Invoke();
    }

    private void DeleteShift(EditableShift? target)
    {
        if (target is null) return;
        target.IsDeleted = true;
        if (target.IsNew)
        {
            target.PropertyChanged -= OnEditablePropertyChanged;
            Editables.Remove(target);
        }
        else
        {
            target.IsDirty = true;
        }
        UpdateCoverageHint();
        RebuildTimeline();
        DirtyStateChanged?.Invoke();
    }

    // ── 快速模板 ──────────────────────────────────────────────────

    private void ApplyTemplate(ShiftTemplate? template)
    {
        if (template is null) return;

        var toRemove = Editables.Where(e => e.IsNew).ToList();
        foreach (var es in toRemove)
        {
            es.PropertyChanged -= OnEditablePropertyChanged;
            Editables.Remove(es);
        }
        foreach (var es in Editables.Where(e => !e.IsDeleted))
        {
            es.IsDeleted = true;
            es.IsDirty = true;
        }

        var today = DateOnly.FromDateTime(LocalNow);
        var displayOrder = Editables.Count + 1;
        foreach (var ts in template.Shifts)
        {
            var es = new EditableShift
            {
                Id = 0,
                Code = ts.Code,
                Name = ts.Name,
                StartTime = ts.StartTime,
                EndTime = ts.EndTime,
                TargetCount = ts.TargetCount,
                ColorHex = ts.ColorHex,
                DisplayOrder = displayOrder++,
                EffectiveFrom = today,
                EffectiveTo = null,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
                IsDirty = true,
                IsNew = true,
            };
            es.PropertyChanged += OnEditablePropertyChanged;
            Editables.Add(es);
        }

        _toast.Show($"已应用模板「{template.Name}」,点击「保存并应用」生效", ToastSeverity.Info);
        UpdateCoverageHint();
        RebuildTimeline();
        DirtyStateChanged?.Invoke();
    }

    private static IReadOnlyList<ShiftTemplate> BuildBuiltInTemplates() => new[]
    {
        new ShiftTemplate(
            "单班制",
            "08:00 – 17:00 · 1 班",
            new[]
            {
                new TemplateShift("A", "白班", "08:00", "17:00", 1500, "#00D4FF"),
            }),
        new ShiftTemplate(
            "两班倒",
            "白班 + 夜班 · 各 12 小时",
            new[]
            {
                new TemplateShift("A", "白班", "08:00", "20:00", 1500, "#FF8C42"),
                new TemplateShift("B", "夜班", "20:00", "08:00", 1200, "#4A9EFF"),
            }),
        new ShiftTemplate(
            "三班倒",
            "早 / 中 / 晚 · 各 8 小时",
            new[]
            {
                new TemplateShift("A", "早班", "00:00", "08:00", 1000, "#AA55FF"),
                new TemplateShift("B", "中班", "08:00", "16:00", 1000, "#00D4FF"),
                new TemplateShift("C", "晚班", "16:00", "24:00", 1000, "#FF6B35"),
            }),
        new ShiftTemplate(
            "四班三运转",
            "早 / 中 / 晚 · 各 8 小时 + 轮休",
            new[]
            {
                new TemplateShift("A", "早班", "00:00", "08:00", 1000, "#AA55FF"),
                new TemplateShift("B", "中班", "08:00", "16:00", 1000, "#00D4FF"),
                new TemplateShift("C", "晚班", "16:00", "24:00", 1000, "#FF6B35"),
            }),
    };

    // ── 时间轴 + 覆盖检测 ─────────────────────────────────────────

    private void OnEditablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableShift.IsDirty)) return;
        if (sender is EditableShift es && !es.IsDirty) es.IsDirty = true;
        UpdateCoverageHint();
        RebuildTimeline();
        _auth.NotifyActivity();
        DirtyStateChanged?.Invoke();
    }

    private void RefreshNowMarker()
    {
        var now = LocalNow;
        NowMarkerLeftPercent =
            (now.Hour * 3600 + now.Minute * 60 + now.Second) * 100.0 / (24 * 3600);
        NowMarkerTimeText = $"NOW {now:HH:mm:ss}";
    }

    private void RebuildTimeline()
    {
        TimelineSegments.Clear();
        foreach (var es in Editables.Where(s => !s.IsDeleted))
        {
            if (!TryParseTime(es.StartTime, out var start)) continue;
            if (!TryParseTime(es.EndTime, out var end)) continue;

            var startPct = (start.Hour * 60 + start.Minute) * 100.0 / (24 * 60);
            var endPct = (end.Hour * 60 + end.Minute) * 100.0 / (24 * 60);

            if (end > start)
            {
                TimelineSegments.Add(new TimelineSegment(
                    es.Code, es.Name, es.ColorHex, startPct, endPct - startPct));
            }
            else
            {
                // 跨日:拆成两段。左段(0:00 → end)归"次日",显式标"(跨日)";
                // 右段(start → 24:00)是当天开始的部分,带箭头延伸到次日
                TimelineSegments.Add(new TimelineSegment(
                    es.Code, $"{es.Name} →", es.ColorHex, startPct, 100.0 - startPct));
                if (endPct > 0)
                {
                    TimelineSegments.Add(new TimelineSegment(
                        es.Code, $"← {es.Name} (跨日)", es.ColorHex, 0, endPct));
                }
            }
        }

        RefreshNowMarker();
    }

    private void UpdateCoverageHint()
    {
        var live = Editables.Where(e => !e.IsDeleted).ToList();
        var totalMin = 0;
        var hasInvalid = false;
        var totalTarget = 0;
        foreach (var s in live)
        {
            totalTarget += s.TargetCount;
            if (!TryParseTime(s.StartTime, out var start) || !TryParseTime(s.EndTime, out var end))
            {
                hasInvalid = true;
                continue;
            }
            totalMin += ComputeDurationMinutes(start, end);
        }

        if (hasInvalid)
        {
            CoverageHint = "有时间格式不合法";
            IsCoverageValid = false;
        }
        else if (totalMin == 24 * 60)
        {
            CoverageHint = $"覆盖完整 24 小时 · 无重叠 · 无空档";
            IsCoverageValid = true;
        }
        else if (totalMin < 24 * 60)
        {
            CoverageHint = $"未覆盖 24 小时(空缺 {(24 * 60 - totalMin) / 60} 时 {(24 * 60 - totalMin) % 60} 分)";
            IsCoverageValid = false;
        }
        else
        {
            CoverageHint = $"班次时段重叠(累计 {totalMin / 60} 时 {totalMin % 60} 分)";
            IsCoverageValid = false;
        }

        CoverageSummary = $"共 {live.Count} 个班次 · 总目标 {totalTarget:N0} 件/天";
    }

    private static bool TryParseTime(string s, out TimeOnly t)
        => TimeOnly.TryParseExact(s, "HH:mm", out t);

    /// <summary>计算 start → end 跨过的分钟数(允许跨 0 点)。EditableShift.DurationLabel 也用此方法。</summary>
    public static int ComputeDurationMinutes(TimeOnly start, TimeOnly end)
    {
        var startMin = start.Hour * 60 + start.Minute;
        var endMin = end.Hour * 60 + end.Minute;
        var diff = endMin - startMin;
        if (diff <= 0) diff += 24 * 60;
        return diff;
    }

    // ── 保存 ──────────────────────────────────────────────────────

    /// <summary>
    /// 保存所有 dirty / deleted 的班次定义。校验失败时显示 toast 并返回 false。
    /// 成功后内部调用 ShiftManager.RefreshAsync 让运行中的服务感知最新班次。
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        foreach (var es in Editables.Where(e => !e.IsDeleted))
        {
            if (!TryParseTime(es.StartTime, out _) || !TryParseTime(es.EndTime, out _))
            {
                _toast.ShowWarning($"班次 [{es.Code}] 时间格式不合法,需 HH:mm");
                return false;
            }
            if (es.TargetCount < 0)
            {
                _toast.ShowWarning($"班次 [{es.Code}] 目标产量不能为负");
                return false;
            }
            if (string.IsNullOrWhiteSpace(es.Name))
            {
                _toast.ShowWarning($"班次 [{es.Code}] 名称不能为空");
                return false;
            }
        }

        var changes = new ShiftDefinitionChanges(
            DeletedIds: Editables.Where(e => e.IsDeleted && e.Id > 0).Select(e => e.Id).ToList(),
            Added: Editables.Where(e => !e.IsDeleted && e.IsDirty && e.IsNew).Select(ToDefinition).ToList(),
            Updated: Editables.Where(e => !e.IsDeleted && e.IsDirty && !e.IsNew).Select(ToDefinition).ToList());

        await _configuration.SaveShiftDefinitionsAsync(changes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>把界面上的可编辑班次转换成领域模型(时间已在保存前校验过格式)。</summary>
    private static ShiftDefinition ToDefinition(EditableShift es)
    {
        TryParseTime(es.StartTime, out var start);
        TryParseTime(es.EndTime, out var end);
        return new ShiftDefinition(
            Id: es.IsNew ? 0 : es.Id,
            Code: es.Code,
            Name: es.Name.Trim(),
            StartTime: start,
            EndTime: end,
            CrossesMidnight: start >= end,
            TargetCount: es.TargetCount,
            ColorHex: es.ColorHex,
            DisplayOrder: es.DisplayOrder,
            EffectiveFrom: es.EffectiveFrom,
            EffectiveTo: es.EffectiveTo,
            CreatedAt: es.CreatedAt);
    }
}

/// <summary>24h 时间轴的一段(UI 用 LeftPercent + WidthPercent 直接定位)。</summary>
public sealed record TimelineSegment(
    string Code,
    string Label,
    string ColorHex,
    double LeftPercent,
    double WidthPercent);

/// <summary>快速模板(单班/两班/三班/四班等),一键应用替换当前所有班次。</summary>
public sealed record ShiftTemplate(
    string Name,
    string Description,
    IReadOnlyList<TemplateShift> Shifts);

/// <summary>模板里的一个班次定义。</summary>
public sealed record TemplateShift(
    string Code,
    string Name,
    string StartTime,
    string EndTime,
    int TargetCount,
    string ColorHex);

/// <summary>包装 ShiftDefinition 用于双向绑定。</summary>
public sealed partial class EditableShift : ObservableObject
{
    /// <summary>对应 DB 主键。0 表示尚未持久化(IsNew=true 时)。</summary>
    public int Id { get; init; }

    /// <summary>班次代号(A / B / C 等),Code 不可编辑(改了会破坏外键关联)。</summary>
    public string Code { get; set; } = "";

    /// <summary>班次配色(十六进制,如 "#FF8C42"),用于时间轴 + Hero 卡渐变。</summary>
    public string ColorHex { get; set; } = "";

    /// <summary>显示顺序(数字小排前),决定 UI 列表与时间轴的左右顺序。</summary>
    public int DisplayOrder { get; set; }

    /// <summary>班次生效起始日期。当天起按本定义判断班次归属。</summary>
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>班次生效结束日期(null = 永久有效)。SoftDelete 时填入。</summary>
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>记录创建时间(UTC),用于审计。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>未持久化的新班次。Save 时走 Insert 路径。</summary>
    public bool IsNew { get; set; }

    /// <summary>被标记删除。Save 时走 SoftDelete。UI 用来灰显 / 隐藏。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>班次显示名称(如 "白班" / "夜班")。</summary>
    [ObservableProperty] public partial string Name { get; set; } = "";

    /// <summary>班次开始时刻 "HH:mm"。</summary>
    [ObservableProperty] public partial string StartTime { get; set; } = "00:00";

    /// <summary>班次结束时刻 "HH:mm"。若 EndTime ≤ StartTime 视为跨日班次。</summary>
    [ObservableProperty] public partial string EndTime { get; set; } = "00:00";

    /// <summary>本班次的目标产量(件)。Save 时落到 ShiftDefinition.TargetCount。</summary>
    [ObservableProperty] public partial int TargetCount { get; set; }

    /// <summary>是否有未保存改动。任一可观察字段变化时由外层置 true。</summary>
    [ObservableProperty] public partial bool IsDirty { get; set; }

    /// <summary>是否跨日(根据 StartTime / EndTime 实时计算,用于 UI 显示标签)。</summary>
    public bool CrossesMidnight
    {
        get
        {
            if (!TimeOnly.TryParseExact(StartTime, "HH:mm", out var s)) return false;
            if (!TimeOnly.TryParseExact(EndTime, "HH:mm", out var e)) return false;
            return s >= e;
        }
    }

    /// <summary>时长描述(用于 UI 显示),例如 "12 小时 0 分"。</summary>
    public string DurationLabel
    {
        get
        {
            if (!TimeOnly.TryParseExact(StartTime, "HH:mm", out var s)) return "--";
            if (!TimeOnly.TryParseExact(EndTime, "HH:mm", out var e)) return "--";
            var mins = ShiftDefinitionEditor.ComputeDurationMinutes(s, e);
            return $"{mins / 60} 小时 {mins % 60} 分";
        }
    }

    /// <summary>从持久化的 <see cref="ShiftDefinition"/> 构造 Editable 包装(IsNew / IsDirty / IsDeleted 默认 false)。</summary>
    public static EditableShift From(ShiftDefinition def) => new()
    {
        Id = def.Id,
        Code = def.Code,
        Name = def.Name,
        StartTime = def.StartTime.ToString("HH:mm"),
        EndTime = def.EndTime.ToString("HH:mm"),
        TargetCount = def.TargetCount,
        ColorHex = def.ColorHex,
        DisplayOrder = def.DisplayOrder,
        EffectiveFrom = def.EffectiveFrom,
        EffectiveTo = def.EffectiveTo,
        CreatedAt = def.CreatedAt,
    };

    partial void OnStartTimeChanged(string value)
    {
        OnPropertyChanged(nameof(CrossesMidnight));
        OnPropertyChanged(nameof(DurationLabel));
    }

    partial void OnEndTimeChanged(string value)
    {
        OnPropertyChanged(nameof(CrossesMidnight));
        OnPropertyChanged(nameof(DurationLabel));
    }
}
