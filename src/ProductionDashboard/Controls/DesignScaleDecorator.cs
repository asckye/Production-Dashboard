using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionDashboard.Controls;

/// <summary>
/// 按设计尺寸等比缩放整个界面。大屏按 <see cref="DesignWidth"/> × <see cref="DesignHeight"/>(默认 1920×1080)设计,
/// 窗口更小时整体缩小、更大(2K / 4K 且系统缩放 100%)时整体放大,字号与卡片比例保持不变。
/// <para>
/// 缩放比例取宽、高两个方向中较小的那个,保证设计尺寸完整放得下;另一个方向多出来的空间
/// 交给子元素自己布局(例如 16:10 屏幕上内容区会更高一些),所以不会出现黑边,也不会被截断。
/// </para>
/// <para>
/// 实现方式:子元素按"可用尺寸 ÷ 比例"做布局,再通过 <see cref="UIElement.RenderTransform"/> 缩放到实际大小。
/// 因此子元素自己的 RenderTransform 会被本控件占用。弹出层(下拉框、提示)和对话框是独立窗口,不受影响。
/// </para>
/// </summary>
public sealed class DesignScaleDecorator : Decorator
{
    private readonly ScaleTransform _scale = new();

    /// <summary><see cref="DesignWidth"/> 依赖属性。</summary>
    public static readonly DependencyProperty DesignWidthProperty =
        DependencyProperty.Register(
            nameof(DesignWidth),
            typeof(double),
            typeof(DesignScaleDecorator),
            new FrameworkPropertyMetadata(1920.0, FrameworkPropertyMetadataOptions.AffectsMeasure),
            IsPositiveSize);

    /// <summary>设计宽度(DIP),比例为 1 时内容按这个宽度排版。</summary>
    public double DesignWidth
    {
        get => (double)GetValue(DesignWidthProperty);
        set => SetValue(DesignWidthProperty, value);
    }

    /// <summary><see cref="DesignHeight"/> 依赖属性。</summary>
    public static readonly DependencyProperty DesignHeightProperty =
        DependencyProperty.Register(
            nameof(DesignHeight),
            typeof(double),
            typeof(DesignScaleDecorator),
            new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsMeasure),
            IsPositiveSize);

    /// <summary>设计高度(DIP),比例为 1 时内容按这个高度排版。</summary>
    public double DesignHeight
    {
        get => (double)GetValue(DesignHeightProperty);
        set => SetValue(DesignHeightProperty, value);
    }

    /// <summary>当前缩放比例(1 = 设计尺寸)。只读,便于调试和测试。</summary>
    public double Scale => _scale.ScaleX;

    /// <summary>
    /// 根据可用尺寸计算缩放比例。某个方向不受限制(无穷大)时只看另一个方向,两个方向都不受限制时为 1。
    /// </summary>
    public static double ComputeScale(Size available, double designWidth, double designHeight)
    {
        var byWidth = IsUsable(available.Width) ? available.Width / designWidth : double.PositiveInfinity;
        var byHeight = IsUsable(available.Height) ? available.Height / designHeight : double.PositiveInfinity;
        var scale = Math.Min(byWidth, byHeight);
        return double.IsInfinity(scale) ? 1.0 : scale;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        var child = Child;
        if (child is null) return default;

        var scale = ComputeScale(constraint, DesignWidth, DesignHeight);
        child.Measure(new Size(constraint.Width / scale, constraint.Height / scale));

        // 受限的方向占满可用空间;不受限的方向按子元素需要的尺寸
        return new Size(
            double.IsInfinity(constraint.Width) ? child.DesiredSize.Width * scale : constraint.Width,
            double.IsInfinity(constraint.Height) ? child.DesiredSize.Height * scale : constraint.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var child = Child;
        if (child is null) return arrangeSize;

        var scale = ComputeScale(arrangeSize, DesignWidth, DesignHeight);
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;
        if (!ReferenceEquals(child.RenderTransform, _scale)) child.RenderTransform = _scale;

        child.Arrange(new Rect(0, 0, arrangeSize.Width / scale, arrangeSize.Height / scale));
        return arrangeSize;
    }

    private static bool IsUsable(double length) => !double.IsInfinity(length) && !double.IsNaN(length) && length > 0;

    private static bool IsPositiveSize(object value) => value is double d && d > 0 && !double.IsInfinity(d);
}
