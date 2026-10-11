using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using ProductionDashboard.Configuration;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.ViewModels;
using ProductionDashboard.Views;

namespace ProductionDashboard;

/// <summary>
/// 大屏主窗口。负责:
/// <list type="bullet">
///   <item>根据 <see cref="DisplayOptions.Fullscreen"/> 切换全屏 / 窗口模式</item>
///   <item>Esc 触发密码弹窗,验证退出密码(配置项 <c>Display:ExitPassword</c>)后关闭</item>
///   <item>鼠标静止超过 <see cref="DisplayOptions.CursorHideAfterMs"/> 毫秒后自动隐藏光标</item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private readonly DisplayOptions _displayOptions;
    private readonly IAuthService _authService;
    private DispatcherTimer? _cursorIdleTimer;   // 仅全屏模式启用,可为 null
    private DateTime _lastMouseMoveAt = DateTime.UtcNow;
    private bool _cursorHidden;

    /// <summary>注入主 view-model + 显示选项 + 鉴权服务,设置全屏模式 / Esc 退出 / 鼠标自动隐藏。</summary>
    public MainWindow(MainViewModel viewModel, IOptions<DisplayOptions> displayOptions, IAuthService authService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _displayOptions = displayOptions.Value;
        _authService = authService;

        ApplyDisplayMode();
        Title = string.IsNullOrWhiteSpace(_displayOptions.FactoryName)
            ? "生产看板"
            : $"生产看板 · {_displayOptions.FactoryName}";

        // 全局键盘钩子 + 鼠标移动钩子
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += OnPreviewMouseMove;

        // 光标自动隐藏:只在全屏(生产)模式下启用。
        // 窗口模式 = 开发期,不隐藏免得调试时看不到鼠标。
        if (_displayOptions.Fullscreen)
        {
            _cursorIdleTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(1),
                DispatcherPriority.Background,
                OnCursorIdleTick,
                Dispatcher);
            _cursorIdleTimer.Start();
        }
    }

    private void ApplyDisplayMode()
    {
        if (_displayOptions.Fullscreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = false; // 不抢占,出问题时还能 Alt+Tab 切走
        }
        else
        {
            // 开发模式仍然最大化(留窗口边框便于调试),
            // 1920×1080 设计的 UI 在任意分辨率上都正确填充工作区。
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.CanResize;
        }
    }

    // ── Esc → 密码退出 ─────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        TryExit();
    }

    private void TryExit()
    {
        // 在弹出密码框前先恢复光标,免得操作员看不到
        ShowCursor();

        var prompt = new ExitPasswordWindow(_authService)
        {
            Owner = this,
        };
        if (prompt.ShowDialog() == true)
        {
            Close();
        }
    }

    // ── 光标自动隐藏 ───────────────────────────────────────────────

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        _lastMouseMoveAt = DateTime.UtcNow;
        if (_cursorHidden) ShowCursor();
    }

    private void OnCursorIdleTick(object? sender, EventArgs e)
    {
        if (_cursorHidden) return;
        var idle = (DateTime.UtcNow - _lastMouseMoveAt).TotalMilliseconds;
        if (idle >= _displayOptions.CursorHideAfterMs)
        {
            HideCursor();
        }
    }

    private void HideCursor()
    {
        Mouse.OverrideCursor = Cursors.None;
        _cursorHidden = true;
    }

    private void ShowCursor()
    {
        Mouse.OverrideCursor = null;
        _cursorHidden = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cursorIdleTimer?.Stop();
        ShowCursor(); // 退出前恢复
        base.OnClosed(e);
    }
}
