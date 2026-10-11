using System.Windows.Controls;

namespace ProductionDashboard.Views.Dashboard;

/// <summary>
/// Dashboard 主视图的「5 维度大数字卡」容器(本小时 / 今日 / 本周 / 本月 / 本年)。
/// 由 <see cref="DashboardView"/> 在 Row 2 装载,DataContext 继承父视图的
/// <see cref="ViewModels.DashboardViewModel"/>,绑定走 <c>Metrics.*</c> 路径。
/// </summary>
public partial class FiveDimensionGrid : UserControl
{
    /// <summary>设计期 / XAML 加载用的默认构造。DataContext 由父视图(DashboardView)继承传入。</summary>
    public FiveDimensionGrid()
    {
        InitializeComponent();
    }
}
