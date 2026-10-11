using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ProductionDashboard.Services.UI;
using ProductionDashboard.ViewModels;

namespace ProductionDashboard.Views;

/// <summary>
/// 右下角 Toast 栈宿主。DataContext 绑到 <see cref="ToastService"/>,
/// 这样 XAML 里 {Binding Items} 就是 ObservableCollection&lt;ToastItem&gt;。
/// </summary>
public partial class ToastHost : UserControl
{
    /// <summary>注入 ToastService 并把它作为 DataContext,XAML 里 {Binding Items} 直接绑定。</summary>
    public ToastHost(ToastService toastService)
    {
        InitializeComponent();
        DataContext = toastService;
    }

    /// <summary>
    /// 单条 toast 卡片 Loaded:启动底部进度条的 ScaleX 1→0 动画。
    /// 动画 Duration = <see cref="ToastItem.Duration"/>,视觉上进度条走完 = 自动消失瞬间。
    /// </summary>
    /// <remarks>
    /// 实现说明:不要用 <c>card.FindName("ProgressScale")</c> 查找 ScaleTransform ——
    /// DataTemplate 的 namescope 对 Freezable 的 x:Name 注册不可靠(WPF 已知问题),经常返回 null,
    /// 动画就不会启动。这里改从可视化树找名为 "ProgressBar" 的 Border,
    /// 再读它的 <see cref="UIElement.RenderTransform"/> 拿到 ScaleTransform。
    /// </remarks>
    private void OnToastLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.DataContext is not ToastItem item) return;
        if (item.Duration <= TimeSpan.Zero) return;

        if (FindDescendantNamed(card, "ProgressBar") is not Border bar) return;

        // 关键:不要复用 XAML 里内联声明的 ScaleTransform。
        // WPF 在 DataTemplate 实例化的某些路径上会把它标记成 frozen,
        // 直接 BeginAnimation 会抛 "对象已密封或已冻结" 的 InvalidOperationException。
        // 解法 — 这里替换成一个全新的、未冻结的 ScaleTransform,再启动动画。
        var fresh = new ScaleTransform(1.0, 1.0);
        bar.RenderTransformOrigin = new Point(0, 0.5);
        bar.RenderTransform = fresh;

        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(item.Duration),
            // 线性最直观:进度条匀速消退,跟剩余时间挂钩
        };
        fresh.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    /// <summary>
    /// 在可视化树中按 <c>x:Name</c> 递归查找子元素。
    /// 比 <see cref="FrameworkElement.FindName"/> 更可靠 — 不依赖 DataTemplate 的 namescope。
    /// </summary>
    private static FrameworkElement? FindDescendantNamed(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return fe;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantNamed(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }
}
