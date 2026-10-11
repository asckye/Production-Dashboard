using System.Windows.Controls;

namespace ProductionDashboard.Views.Dashboard;

/// <summary>
/// Dashboard 主视图的「当前班次 Hero 卡」UserControl。
/// 由 <see cref="DashboardView"/> 在 Row 0 Column 0 装载,DataContext 继承父视图的
/// <see cref="ViewModels.DashboardViewModel"/>,绑定走 <c>Hero.*</c> 路径。
/// </summary>
public partial class HeroCard : UserControl
{
    /// <summary>设计期 / XAML 加载用的默认构造。DataContext 由父视图(DashboardView)继承传入。</summary>
    public HeroCard()
    {
        InitializeComponent();
    }
}
