namespace ProductionDashboard.Services.UI;

/// <summary>
/// Toast 提示通道。任何后台线程都可以调用 Show* 方法,
/// 实现负责 marshal 到 UI Dispatcher 后挂到 ToastHost 上,过期自动消失。
/// 替代旧的"顶部红条 banner"和 ShiftConfig 底部 StatusMessage。
/// </summary>
public interface IToastService
{
    /// <summary>显示一条 toast。message 为空字符串时不显示(防御性)。</summary>
    void Show(string message, ToastSeverity severity = ToastSeverity.Info, string? title = null, TimeSpan? duration = null);

    /// <summary>便捷重载:成功提示。默认 4 秒后消失。</summary>
    void ShowSuccess(string message, string? title = null);

    /// <summary>便捷重载:警告。默认 6 秒后消失。</summary>
    void ShowWarning(string message, string? title = null);

    /// <summary>便捷重载:错误。默认 8 秒后消失。</summary>
    void ShowError(string message, string? title = null);

    /// <summary>立即清空所有 toast(主要用于退出 / 切视图前)。</summary>
    void Clear();
}

/// <summary>
/// Toast 严重级别,决定颜色 / 图标 / 默认存活时长。
/// </summary>
public enum ToastSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}
