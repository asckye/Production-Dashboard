using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProductionDashboard.Services.UI;

namespace ProductionDashboard.ViewModels;

/// <summary>
/// 单条 toast 的 view-model。包含一个 DispatcherTimer 做自动消失,
/// 用户点关闭按钮也走同一通道(<see cref="DismissRequested"/>)。
/// </summary>
public sealed partial class ToastItem : ObservableObject
{
    /// <summary>正文消息(必填)。</summary>
    public string Message { get; }
    /// <summary>标题(null 时按 Severity 取默认值,见 <see cref="DefaultTitle"/>)。</summary>
    public string? Title { get; }
    /// <summary>严重级别:决定颜色 / 图标 / 默认标题。</summary>
    public ToastSeverity Severity { get; }
    /// <summary>创建时间戳,用于 UI 显示"刚刚 / 1 分钟前"等相对时间(目前未启用)。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>预定存活时长。底部进度条根据此值做线性缩减动画。</summary>
    public TimeSpan Duration { get; }

    /// <summary>用于 DataTemplate 选择器:严重级别的字符串名,等价于 enum.ToString()。</summary>
    public string SeverityKey => Severity.ToString();

    /// <summary>顶部右上角的图标符号。仿照 Material / Win11 通知样式。</summary>
    public string Icon => Severity switch
    {
        ToastSeverity.Success => "✓",
        ToastSeverity.Warning => "⚠",
        ToastSeverity.Error => "✕",
        _ => "ⓘ",
    };

    /// <summary>当前是否已请求关闭。第二次触发会被忽略,避免重复 Remove。</summary>
    [ObservableProperty] public partial bool IsDismissed { get; private set; }

    public IRelayCommand DismissCommand { get; }

    public event EventHandler? DismissRequested;

    private DispatcherTimer? _autoTimer;

    public ToastItem(string message, ToastSeverity severity, string? title, DateTimeOffset timestamp, TimeSpan duration)
    {
        Message = message;
        Severity = severity;
        Title = title ?? DefaultTitle(severity);
        Timestamp = timestamp;
        Duration = duration;
        DismissCommand = new RelayCommand(Dismiss);
    }

    /// <summary>启动自动消失计时(在 Dispatcher 线程上调用)。</summary>
    public void StartAutoDismiss()
    {
        if (_autoTimer is not null) return;
        _autoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Duration };
        _autoTimer.Tick += (_, _) => Dismiss();
        _autoTimer.Start();
    }

    /// <summary>触发关闭。重复调用安全 — 已关闭直接返回。</summary>
    public void Dismiss()
    {
        if (IsDismissed) return;
        IsDismissed = true;
        _autoTimer?.Stop();
        _autoTimer = null;
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string DefaultTitle(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => "成功",
        ToastSeverity.Warning => "警告",
        ToastSeverity.Error => "错误",
        _ => "提示",
    };
}
