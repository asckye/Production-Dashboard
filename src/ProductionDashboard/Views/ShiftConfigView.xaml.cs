using System.Windows.Controls;
using ProductionDashboard.ViewModels;

namespace ProductionDashboard.Views;

/// <summary>班次配置页(就地编辑现有班次的名称/起止/目标)。</summary>
public partial class ShiftConfigView : UserControl
{
    /// <summary>注入 ShiftConfigViewModel 作为 DataContext。</summary>
    public ShiftConfigView(ShiftConfigViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
