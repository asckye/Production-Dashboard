using System.Windows.Controls;

namespace ProductionDashboard.Views.Dashboard;

/// <summary>
/// Dashboard 主视图的「白班 vs 夜班 对比卡」UserControl,含累计对比表。
/// 由 <see cref="DashboardView"/> 在 Row 0 Column 2 装载,DataContext 继承父视图的
/// <see cref="ViewModels.DashboardViewModel"/>,绑定走 <c>Hero.*</c> 与 <c>Metrics.*</c> 路径。
/// </summary>
public partial class ShiftCompareCard : UserControl
{
    /// <summary>设计期 / XAML 加载用的默认构造。DataContext 由父视图(DashboardView)继承传入。</summary>
    public ShiftCompareCard()
    {
        InitializeComponent();
    }
}
