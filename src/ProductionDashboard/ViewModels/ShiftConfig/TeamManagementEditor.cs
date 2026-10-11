using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Services.UI;

namespace ProductionDashboard.ViewModels.ShiftConfig;

/// <summary>
/// 班组管理子编辑器:仅负责班组名称 + 班长姓名的双向编辑。
/// 槽位 ↔ 班组分配由 <see cref="ITeamAssignmentService"/> 在别处管理(倒班窗口),
/// 这里不涉及。
/// </summary>
public sealed class TeamManagementEditor
{
    private readonly IShiftConfigurationService _configuration;
    private readonly ITeamAssignmentService _teamAssign;
    private readonly IToastService _toast;
    private readonly IAuthService _auth;

    /// <summary>注入 DB / 缓存 / 提示 / 鉴权 / 日志依赖。集合在 <see cref="Reload"/> 调用后才会填充。</summary>
    public TeamManagementEditor(
        IShiftConfigurationService configuration,
        ITeamAssignmentService teamAssign,
        IToastService toast,
        IAuthService auth)
    {
        _configuration = configuration;
        _teamAssign = teamAssign;
        _toast = toast;
        _auth = auth;

        EditableTeams = new ObservableCollection<EditableTeam>();
    }

    /// <summary>当前班组的可编辑视图。Save 时把每个 dirty 的班组写回 DB + 通知 TeamAssignmentService。</summary>
    public ObservableCollection<EditableTeam> EditableTeams { get; }

    /// <summary>有任何 dirty 班组时触发,facade 据此聚合 HasUnsavedChanges。</summary>
    public event Action? DirtyStateChanged;

    /// <summary>是否有未保存改动(任一 dirty)。facade 据此聚合 Save 按钮的 CanExecute。</summary>
    public bool HasUnsavedChanges => EditableTeams.Any(t => t.IsDirty);

    /// <summary>从 TeamAssignmentService 重新加载班组到 EditableTeams(放弃任何未保存改动)。</summary>
    public void Reload()
    {
        foreach (var t in EditableTeams) t.PropertyChanged -= OnTeamPropertyChanged;
        EditableTeams.Clear();
        foreach (var team in _teamAssign.Teams.OrderBy(t => t.DisplayOrder))
        {
            var et = EditableTeam.From(team);
            et.PropertyChanged += OnTeamPropertyChanged;
            EditableTeams.Add(et);
        }
    }

    private void OnTeamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableTeam.IsDirty)) return;
        if (sender is EditableTeam et && !et.IsDirty) et.IsDirty = true;
        // 任何编辑都视为活动 → 续期空闲计时,避免编辑中途被强制登出
        _auth.NotifyActivity();
        DirtyStateChanged?.Invoke();
    }

    /// <summary>保存前校验所有 dirty 班组。发现问题时显示 toast 并返回 false。</summary>
    public bool Validate()
    {
        foreach (var et in EditableTeams.Where(t => t.IsDirty))
        {
            if (string.IsNullOrWhiteSpace(et.Name))
            {
                _toast.ShowWarning($"班组 [Id={et.Id}] 名称不能为空");
                return false;
            }
            if (string.IsNullOrWhiteSpace(et.LeaderName))
            {
                _toast.ShowWarning($"班组 [{et.Name}] 班长姓名不能为空");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 保存所有 dirty 班组。校验失败时显示 toast 并返回 false(让 facade 中断后续保存)。
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        var dirty = EditableTeams.Where(t => t.IsDirty).ToList();
        if (dirty.Count == 0) return true;
        if (!Validate()) return false;

        // 成员名单(Members)不在界面上编辑,沿用原值
        var updated = dirty
            .Select(et => et.OriginalSnapshot with { Name = et.Name.Trim(), LeaderName = et.LeaderName.Trim() })
            .ToList();
        await _configuration.SaveTeamsAsync(updated, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>当前 dirty 班组数量,用于显示 toast 摘要 / 调试。</summary>
    public int DirtyCount => EditableTeams.Count(t => t.IsDirty);

    /// <summary>Save 成功后调用,把所有 Editable 的 IsDirty 置回 false。</summary>
    public void ClearDirty()
    {
        foreach (var t in EditableTeams) t.IsDirty = false;
    }
}

/// <summary>班组的可编辑视图(双向绑定)。
/// 成员名单(Members)字段不在 UI 暴露,保留 OriginalSnapshot 里的原值以兼容数据库列。</summary>
public sealed partial class EditableTeam : ObservableObject
{
    /// <summary>对应 DB 主键(Teams 表)。</summary>
    public int Id { get; init; }
    /// <summary>班组配色(十六进制,如 "#FF8C42")。当前 UI 不编辑,只透传。</summary>
    public string ColorHex { get; set; } = "";
    /// <summary>显示顺序(数字小排前)。</summary>
    public int DisplayOrder { get; set; }
    /// <summary>原始数据快照,Save 时用于构造 with-update 的 Team record(保留未被编辑的字段)。</summary>
    public Team OriginalSnapshot { get; init; } = null!;

    /// <summary>班组名称("甲班" / "乙班" / "丙班" 等)。</summary>
    [ObservableProperty] public partial string Name { get; set; } = "";
    /// <summary>班长姓名,显示在 Hero 卡 + 班组管理列表。</summary>
    [ObservableProperty] public partial string LeaderName { get; set; } = "";
    /// <summary>是否有未保存改动。任一可观察字段变化时由外层置 true。</summary>
    [ObservableProperty] public partial bool IsDirty { get; set; }

    /// <summary>从持久化的 <see cref="Team"/> 构造 Editable 包装(IsDirty 默认 false)。</summary>
    public static EditableTeam From(Team team) => new()
    {
        Id = team.Id,
        ColorHex = team.ColorHex,
        DisplayOrder = team.DisplayOrder,
        OriginalSnapshot = team,
        Name = team.Name,
        LeaderName = team.LeaderName,
    };
}
