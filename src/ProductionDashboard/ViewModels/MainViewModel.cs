using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Configuration;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Services.UI;
using ProductionDashboard.Views;

namespace ProductionDashboard.ViewModels;

/// <summary>
/// 主窗口 view-model:顶栏字段 + 主内容区视图切换 (Dashboard / ShiftConfig)。
/// 同时负责登录 / 登出 / 倒班切换,以及顶栏的当前班次与班组显示。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DashboardView _dashboardView;
    private readonly ShiftConfigView _shiftConfigView;
    private readonly ShiftConfigViewModel _shiftConfigViewModel;
    private readonly DispatcherTimer _clockTimer;
    private readonly IAuthService _auth;
    private readonly ITeamAssignmentService _teamAssign;
    private readonly IShiftManager _shifts;
    private readonly IToastService _toast;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _clock;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>窗口标题(默认 "ProductionDashboard",后续可由配置覆盖)。</summary>
    [ObservableProperty] public partial string Title { get; set; } = "ProductionDashboard";
    /// <summary>副标题(预留,目前 UI 未使用)。</summary>
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    /// <summary>顶栏工厂名("示例车间 | 1 号产线" 之类),从 DisplayOptions 读取。</summary>
    [ObservableProperty] public partial string FactoryName { get; set; } = "";

    /// <summary>顶栏当前班次:"A 班 · 白班"(前缀部分,粗体显示)。</summary>
    [ObservableProperty] public partial string CurrentShiftText { get; set; } = "当前班次 加载中…";

    /// <summary>顶栏白班槽位的班组,例如"A 班 · 班长 张三"。倒班后跟人走。</summary>
    [ObservableProperty] public partial string DaySlotTeamText { get; set; } = "白班 加载中…";

    /// <summary>顶栏夜班槽位的班组,例如"B 班 · 班长 李四"。</summary>
    [ObservableProperty] public partial string NightSlotTeamText { get; set; } = "夜班 加载中…";

    /// <summary>顶栏日期文本 "2026年05月23日 星期日"。每秒由 clock timer 刷新。</summary>
    [ObservableProperty] public partial string DateText { get; set; } = "";
    /// <summary>顶栏时间文本 "HH:mm:ss"。每秒由 clock timer 刷新。</summary>
    [ObservableProperty] public partial string TimeText { get; set; } = "";

    /// <summary>主内容区当前视图。</summary>
    [ObservableProperty] public partial UserControl CurrentView { get; set; } = null!;

    /// <summary>是否在 Dashboard 视图(影响顶栏按钮显示)。</summary>
    [ObservableProperty] public partial bool IsOnDashboard { get; set; } = true;

    /// <summary>当前是否登录态(决定倒班切换 / 班组管理按钮的可见性)。</summary>
    [ObservableProperty] public partial bool IsAuthenticated { get; set; }

    /// <summary>登录用户名(用于顶栏显示"班长:张三 [登出]")。</summary>
    [ObservableProperty] public partial string OperatorName { get; set; } = "";

    /// <summary>右下角 toast 提示宿主控件,由 DI 注入并直接绑定到主窗口。</summary>
    public ToastHost ToastHost { get; }

    /// <summary>Dashboard 子 view-model(facade),负责大屏内容区。</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>切回大屏视图。</summary>
    public IRelayCommand ShowDashboardCommand { get; }
    /// <summary>进入班组配置页(未登录时先弹班长登录)。</summary>
    public IRelayCommand ShowSettingsCommand { get; }
    /// <summary>切换深 / 浅主题。</summary>
    public IRelayCommand ToggleThemeCommand { get; }
    /// <summary>打开班长登录对话框。</summary>
    public IRelayCommand LoginCommand { get; }
    /// <summary>登出当前班长(立即触发 OnAuthChanged 退回大屏)。</summary>
    public IRelayCommand LogoutCommand { get; }
    /// <summary>一键对调白班 / 夜班的班组分配(仅在 2 槽位 + 已登录时可用)。</summary>
    public IAsyncRelayCommand SwapShiftsCommand { get; }

    /// <summary>主题切换按钮显示的图标("☀" = 当前 Dark / "🌙" = 当前 Light)。</summary>
    [ObservableProperty] public partial string ThemeToggleIcon { get; set; } = "☀";

    /// <summary>注入 UI / DB / 业务依赖,启动 clock timer,订阅 auth / shifts / theme 事件。</summary>
    public MainViewModel(
        IOptions<DisplayOptions> displayOptions,
        DashboardView dashboardView,
        ShiftConfigView shiftConfigView,
        ToastHost toastHost,
        DashboardViewModel dashboardViewModel,
        ShiftConfigViewModel shiftConfigViewModel,
        IGlobalExceptionHandler exceptionHandler,
        IToastService toastService,
        IThemeService themeService,
        IAuthService authService,
        ITeamAssignmentService teamAssignmentService,
        IShiftManager shifts,
        IDialogService dialogs,
        TimeProvider clock,
        ILogger<MainViewModel> logger)
    {
        FactoryName = displayOptions.Value.FactoryName;
        _dashboardView = dashboardView;
        _shiftConfigView = shiftConfigView;
        _shiftConfigViewModel = shiftConfigViewModel;
        _theme = themeService;
        _toast = toastService;
        _auth = authService;
        _teamAssign = teamAssignmentService;
        _shifts = shifts;
        _dialogs = dialogs;
        _clock = clock;
        _logger = logger;
        ToastHost = toastHost;
        Dashboard = dashboardViewModel;

        CurrentView = _dashboardView;

        ToggleThemeCommand = new RelayCommand(() => _theme.Toggle());
        _theme.ThemeChanged += OnThemeChanged;
        OnThemeChanged(_theme.Current);

        exceptionHandler.ExceptionReported += OnExceptionReported;

        ShowDashboardCommand = new RelayCommand(() =>
        {
            CurrentView = _dashboardView;
            IsOnDashboard = true;
        });

        ShowSettingsCommand = new RelayCommand(OpenShiftConfigGated);

        LoginCommand = new RelayCommand(OpenLoginDialog);
        LogoutCommand = new RelayCommand(() => _auth.Logout());
        SwapShiftsCommand = new AsyncRelayCommand(SwapShiftsAsync, CanSwapShifts);

        _auth.AuthChanged += OnAuthChanged;
        OnAuthChanged();

        _teamAssign.AssignmentsChanged += OnAssignmentsChanged;
        _shifts.DefinitionsChanged += OnAssignmentsChanged;
        _shifts.ShiftChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(UpdateCurrentShiftText);
        OnAssignmentsChanged();   // 启动期立刻刷一次顶栏

        Dashboard.Hero.PropertyChanged += (_, e) =>
        {
            // 当槽位状态、激活态、或槽位上班组的名称变化时,刷新顶栏"当前班次"徽章。
            if (e.PropertyName is nameof(Dashboard.Hero.AStatus) or nameof(Dashboard.Hero.BStatus)
                or nameof(Dashboard.Hero.AIsActive) or nameof(Dashboard.Hero.BIsActive)
                or nameof(Dashboard.Hero.ATeamLabel) or nameof(Dashboard.Hero.BTeamLabel))
            {
                UpdateCurrentShiftText();
            }
        };
        UpdateCurrentShiftText();

        _clockTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => UpdateClock(),
            Dispatcher.CurrentDispatcher);
        _clockTimer.Start();
        UpdateClock();
    }

    private void UpdateClock()
    {
        var now = _clock.GetLocalNow().DateTime;
        DateText = $"{now:yyyy年MM月dd日} 星期{"日一二三四五六"[(int)now.DayOfWeek]}";
        TimeText = now.ToString("HH:mm:ss");
    }

    /// <summary>
    /// 顶栏"当前班次"徽章:显示实际正在进行的班次(不限于参与对比的两个),有班组时显示班组名。
    /// </summary>
    private void UpdateCurrentShiftText()
    {
        var current = _shifts.Current;
        if (current is null)
        {
            CurrentShiftText = "当前班次 空档期";
            return;
        }
        var team = _teamAssign.GetTeamForSlot(current.Definition.Id);
        CurrentShiftText = team is null
            ? $"当前班次 {current.Definition.Name}(未分配班组)"
            : $"当前班次 {team.Name}";
    }

    private void OnAssignmentsChanged()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var (aDef, bDef) = ComparedShifts.Pick(_shifts.Definitions);
            DaySlotTeamText = SlotText(aDef);
            NightSlotTeamText = SlotText(bDef);
            UpdateCurrentShiftText();

            SwapShiftsCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>顶栏班组槽位文字,例如"白班 · A 班 (班长:张三)"。</summary>
    private string SlotText(ShiftDefinition? definition)
    {
        if (definition is null) return "—";
        var team = _teamAssign.GetTeamForSlot(definition.Id);
        return team is null
            ? $"{definition.Name} [未分配班组]"
            : $"{definition.Name} · {team.Name} (班长:{team.LeaderName})";
    }

    private bool _wasAuthenticated;

    private void OnAuthChanged()
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var nowAuthed = _auth.IsAuthenticated;
            IsAuthenticated = nowAuthed;
            OperatorName = _auth.CurrentOperator ?? "";
            SwapShiftsCommand.NotifyCanExecuteChanged();

            // 登录态丢失 (空闲超时 / 手动登出) → 若正在班组配置页,强制切回大屏
            // 避免未登录态停留在敏感配置页继续编辑
            if (_wasAuthenticated && !nowAuthed)
            {
                if (!IsOnDashboard)
                {
                    CurrentView = _dashboardView;
                    IsOnDashboard = true;
                    _toast.ShowWarning(
                        "班长登录已结束(空闲超时或手动登出),已退回大屏",
                        title: "需要重新登录");
                }
            }
            _wasAuthenticated = nowAuthed;
        });
    }

    /// <summary>
    /// 进入"班组配置"的拦截入口:未登录则先弹班长登录,登录成功才切换;取消则保持大屏。
    /// 已登录则直接进入。
    /// </summary>
    private void OpenShiftConfigGated()
    {
        if (!_auth.IsAuthenticated && !_dialogs.ShowLogin())
        {
            return;   // 用户取消登录 → 留在大屏
        }

        _auth.NotifyActivity();
        _shiftConfigViewModel.Reload();
        CurrentView = _shiftConfigView;
        IsOnDashboard = false;
    }

    private void OpenLoginDialog()
    {
        try
        {
            _dialogs.ShowLogin();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "弹出登录对话框失败");
            _toast.ShowError("打开登录对话框失败,请重试");
        }
    }

    private bool CanSwapShifts()
    {
        if (!_auth.IsAuthenticated) return false;
        // 只在恰好 2 个槽位分配时可一键对调,否则要去配置页用"重新分配"
        return _teamAssign.CurrentAssignments.Count == 2;
    }

    private async Task SwapShiftsAsync()
    {
        if (!_auth.IsAuthenticated)
        {
            _toast.ShowWarning("请先登录班长账号");
            return;
        }

        var assignments = _teamAssign.CurrentAssignments;
        if (assignments.Count != 2)
        {
            _toast.ShowWarning("当前槽位数量 ≠ 2,无法一键对调,请去班次配置「重新分配」");
            return;
        }

        // 二次确认 — 把对调前后内容明白展示
        var (aDef, bDef) = ComparedShifts.Pick(_shifts.Definitions);
        if (aDef is null || bDef is null)
        {
            _toast.ShowWarning("班次定义不足两个,无法对调");
            return;
        }
        var aTeam = _teamAssign.GetTeamForSlot(aDef.Id);
        var bTeam = _teamAssign.GetTeamForSlot(bDef.Id);
        if (aTeam is null || bTeam is null)
        {
            _toast.ShowWarning("当前有槽位未分配班组,无法对调");
            return;
        }

        if (!_dialogs.ConfirmShiftSwap(aTeam, bTeam)) return;

        _auth.NotifyActivity();   // 操作中,延长会话

        try
        {
            await _teamAssign.SwapTwoSlotsAsync(_auth.CurrentOperator!, CancellationToken.None);
            _toast.ShowSuccess(
                $"倒班完成 · {aDef.Name} {bTeam.Name} / {bDef.Name} {aTeam.Name}",
                title: $"操作员 {_auth.CurrentOperator}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "倒班对调失败");
            _toast.ShowError(ex.Message, "倒班对调失败");
        }
    }

    private void OnExceptionReported(object? sender, ExceptionReportedEventArgs e)
    {
        var title = $"系统异常 · {e.Source} · {e.Timestamp:HH:mm:ss}";
        _toast.ShowError(e.Message, title);
    }

    private void OnThemeChanged(AppTheme theme)
    {
        ThemeToggleIcon = theme == AppTheme.Dark ? "☀" : "🌙";
    }
}
