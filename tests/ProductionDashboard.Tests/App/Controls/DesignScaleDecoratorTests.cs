using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using ProductionDashboard.Controls;

namespace ProductionDashboard.Tests.App.Controls;

/// <summary>
/// 界面等比缩放容器测试。覆盖:
/// <list type="bullet">
///   <item>缩放比例取宽、高两个方向中较小的一个</item>
///   <item>不受限的方向不参与计算</item>
///   <item>子元素按"可用尺寸 ÷ 比例"布局,既不会小于设计尺寸被截断,也会用满多出来的空间</item>
/// </list>
/// </summary>
public sealed class DesignScaleDecoratorTests
{
    [Theory]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1280, 720, 2.0 / 3)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(1440, 900, 0.75)]       // 16:10,宽度方向受限
    [InlineData(2560, 1080, 1.0)]       // 带鱼屏,高度方向受限
    public void ComputeScale_uses_the_tighter_dimension(double width, double height, double expected)
    {
        DesignScaleDecorator.ComputeScale(new Size(width, height), 1920, 1080)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void ComputeScale_ignores_unbounded_dimension()
    {
        DesignScaleDecorator.ComputeScale(new Size(double.PositiveInfinity, 540), 1920, 1080)
            .Should().BeApproximately(0.5, 1e-9);
        DesignScaleDecorator.ComputeScale(new Size(double.PositiveInfinity, double.PositiveInfinity), 1920, 1080)
            .Should().Be(1.0);
    }

    [Theory]
    [InlineData(1280, 720, 1920, 1080)]
    [InlineData(1440, 900, 1920, 1200)]   // 多出来的高度交给内容
    [InlineData(3840, 2160, 1920, 1080)]
    public void Child_is_laid_out_at_available_size_divided_by_scale(
        double width, double height, double childWidth, double childHeight)
    {
        RunOnSta(() =>
        {
            var child = new Border();
            var decorator = new DesignScaleDecorator { Child = child };

            decorator.Measure(new Size(width, height));
            decorator.Arrange(new Rect(0, 0, width, height));

            child.RenderSize.Width.Should().BeApproximately(childWidth, 1e-6);
            child.RenderSize.Height.Should().BeApproximately(childHeight, 1e-6);
            (child.RenderSize.Width * decorator.Scale).Should().BeApproximately(width, 1e-6);
        });
    }

    /// <summary>WPF 元素只能在 STA 线程上创建和布局。</summary>
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA 线程上的断言失败", error);
    }
}
