using System.Windows.Controls;
using ProductionDashboard.ViewModels;

namespace ProductionDashboard.Views;

/// <summary>
/// 主仪表盘视图。承担 mockup 里 TopBar 下方的所有内容
/// (Hero 当前班次 + A/B 对比 + 5 维度卡)。
/// </summary>
public partial class DashboardView : UserControl
{
    /// <summary>注入 DashboardViewModel 作为 DataContext。</summary>
    public DashboardView(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
