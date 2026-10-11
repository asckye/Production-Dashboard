using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Services.UI;
using ProductionDashboard.ViewModels.ShiftConfig;

namespace ProductionDashboard.ViewModels;

/// <summary>
/// 班组配置页 view-model — facade。把保存动作协调到两个子编辑器:
/// <list type="bullet">
///   <item><see cref="Definitions"/>(<see cref="ShiftDefinitionEditor"/>)— 班次定义 + 时间轴 + 模板</item>
///   <item><see cref="Teams"/>(<see cref="TeamManagementEditor"/>)— 班组名称 / 班长</item>
/// </list>
/// Save 顺序:先 Definitions(成功后 ShiftManager 重载),再 Teams(成功后 TeamAssignmentService 重载)。
/// 任一子编辑器校验失败则中断,toast 在子编辑器内显示。
/// </summary>
public sealed partial class ShiftConfigViewModel : ObservableObject
{
    private readonly IToastService _toast;
    private readonly ILogger<ShiftConfigViewModel> _logger;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;

    /// <summary>注入 DB / 缓存 / 提示 / 鉴权 / 日志依赖,构造两个子编辑器并订阅 dirty 事件。</summary>
    public ShiftConfigViewModel(
        IShiftConfigurationService configuration,
        IShiftManager shifts,
        ITeamAssignmentService teamAssign,
        TimeProvider clock,
        IToastService toast,
        IAuthService auth,
        ILogger<ShiftConfigViewModel> logger)
    {
        _toast = toast;
        _logger = logger;
        _clock = clock;
        _dispatcher = Application.Current.Dispatcher;

        Definitions = new ShiftDefinitionEditor(configuration, shifts, toast, auth, clock, _dispatcher);
        Teams = new TeamManagementEditor(configuration, teamAssign, toast, auth);

        Definitions.DirtyStateChanged += RecalcHasUnsavedChanges;
        Teams.DirtyStateChanged += RecalcHasUnsavedChanges;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasUnsavedChanges);
        CancelCommand = new RelayCommand(Reload);

        Reload();
    }

    /// <summary>班次定义子编辑器(班次行 + 24h 时间轴 + 模板)。</summary>
    public ShiftDefinitionEditor Definitions { get; }
    /// <summary>班组管理子编辑器(班组名 + 班长)。</summary>
    public TeamManagementEditor Teams { get; }

    /// <summary>聚合 dirty 状态:任一子编辑器有未保存改动则为 true。Save 按钮据此启用。</summary>
    [ObservableProperty] public partial bool HasUnsavedChanges { get; set; }

    /// <summary>保存命令:两部分都校验通过后,先保存班次定义,再保存班组。</summary>
    public IAsyncRelayCommand SaveCommand { get; }
    /// <summary>取消命令:从底层服务重新加载,丢弃所有未保存改动。</summary>
    public IRelayCommand CancelCommand { get; }

    private void RecalcHasUnsavedChanges()
    {
        HasUnsavedChanges = Definitions.HasUnsavedChanges || Teams.HasUnsavedChanges;
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从底层服务重新加载班次定义 + 班组,放弃任何未保存改动。</summary>
    public void Reload()
    {
        Definitions.Reload();
        Teams.Reload();
        HasUnsavedChanges = false;
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 保存。由命令在 UI 线程上调用,await 之后也回到 UI 线程,可以直接操作编辑器集合。
    /// <list type="number">
    ///   <item>先校验班次和班组两部分,都通过才写数据库,避免"班次已保存、班组校验失败"的半保存状态</item>
    ///   <item>保存班次(单个事务,失败则数据库不变,可直接重试)</item>
    ///   <item>班次保存成功后立刻重新加载班次编辑器:新增的班次拿到数据库 Id、不再标记为"新增",
    ///         这样即使接下来班组保存失败,再点保存也不会把同样的班次再插入一遍</item>
    ///   <item>保存班组</item>
    /// </list>
    /// </summary>
    private async Task SaveAsync()
    {
        var ct = CancellationToken.None;
        if (!Definitions.Validate() || !Teams.Validate()) return;

        var teamDirtyCount = Teams.DirtyCount;
        try
        {
            if (!await Definitions.SaveAsync(ct)) return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "班次定义保存失败");
            _toast.ShowError(ex.Message, "班次保存失败(未做任何修改,可重试)");
            return;
        }

        Definitions.Reload();
        RecalcHasUnsavedChanges();

        try
        {
            if (!await Teams.SaveAsync(ct)) return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "班组保存失败");
            _toast.ShowError(ex.Message, "班次已保存,班组保存失败(可重试)");
            return;
        }

        Reload();
        var msg = teamDirtyCount > 0
            ? $"班组配置已保存(含 {teamDirtyCount} 个班组改动) · {_clock.GetLocalNow():HH:mm:ss}"
            : $"班组配置已保存 · {_clock.GetLocalNow():HH:mm:ss}";
        _toast.ShowSuccess(msg);
    }
}
