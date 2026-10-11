using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ProductionDashboard.ViewModels;

namespace ProductionDashboard.Services.UI;

/// <summary>
/// 单例 Toast 服务。后台任何线程都可以 Show — 内部 marshal 到 UI Dispatcher。
/// UI 层只需要把 <see cref="Items"/> 绑到 ItemsControl 即可呈现栈。
/// </summary>
public sealed class ToastService : IToastService
{
    private const int MaxStack = 5;

    private static readonly TimeSpan DefaultInfo = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultSuccess = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultWarning = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultError = TimeSpan.FromSeconds(8);

    /// <summary>UI 绑定的 toast 集合。仅在 Dispatcher 线程上修改。</summary>
    public ObservableCollection<ToastItem> Items { get; } = [];

    /// <inheritdoc/>
    public void Show(string message, ToastSeverity severity = ToastSeverity.Info, string? title = null, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var ttl = duration ?? severity switch
        {
            ToastSeverity.Success => DefaultSuccess,
            ToastSeverity.Warning => DefaultWarning,
            ToastSeverity.Error => DefaultError,
            _ => DefaultInfo,
        };

        RunOnDispatcher(() =>
        {
            // 超过栈上限时,弹掉最旧的(队首)
            while (Items.Count >= MaxStack)
            {
                Items[0].Dismiss();
                Items.RemoveAt(0);
            }

            var item = new ToastItem(message.Trim(), severity, title, DateTimeOffset.Now, ttl);
            item.DismissRequested += OnDismissRequested;
            Items.Add(item);
            item.StartAutoDismiss();
        });
    }

    /// <inheritdoc/>
    public void ShowSuccess(string message, string? title = null)
        => Show(message, ToastSeverity.Success, title);

    /// <inheritdoc/>
    public void ShowWarning(string message, string? title = null)
        => Show(message, ToastSeverity.Warning, title);

    /// <inheritdoc/>
    public void ShowError(string message, string? title = null)
        => Show(message, ToastSeverity.Error, title);

    /// <inheritdoc/>
    public void Clear()
    {
        RunOnDispatcher(() =>
        {
            // 先 unsubscribe 防止 Dismiss → OnDismissRequested 再启动一批 420ms 定时器
            // 然后才 Items.Clear() — 否则定时器在 Dispatcher 销毁后 Tick 会抛
            foreach (var item in Items)
            {
                item.DismissRequested -= OnDismissRequested;
                item.Dismiss();
            }
            Items.Clear();
        });
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        if (sender is not ToastItem item) return;
        // 已经在 Dispatcher 线程(ToastItem 的 timer 是 DispatcherTimer)
        item.DismissRequested -= OnDismissRequested;

        // 防御:item 可能已被 Clear()/早期路径从集合移除 — 不必再启动延迟定时器
        if (!Items.Contains(item)) return;

        // 延后 ~420ms 再从集合移除,让 XAML 那边的退场动画(滑出 + 淡出 380ms + 40ms 缓冲)
        // 有时间播完;立刻 Remove 的话 ItemsControl 直接卸载,看不到任何动画。
        var removeAfter = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        removeAfter.Tick += (_, _) =>
        {
            removeAfter.Stop();
            Items.Remove(item);
        };
        removeAfter.Start();
    }

    /// <summary>
    /// Marshal 到 UI 线程。Application.Current 可能为 null(进程刚启动 / 已退出),
    /// 此时静默丢弃 — toast 不是关键路径,不值得抛异常。
    /// </summary>
    private static void RunOnDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
    }
}
