using System.Windows;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Views;

/// <summary>
/// 倒班对调二次确认。展示对调前后白班/夜班的班组归属,避免误点。
/// </summary>
public partial class SwapConfirmationWindow : Window
{
    /// <summary>注入当前白班 / 夜班分配的班组,在窗口里清晰显示对调前后归属。</summary>
    public SwapConfirmationWindow(Team currentDayTeam, Team currentNightTeam)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(currentDayTeam);
        ArgumentNullException.ThrowIfNull(currentNightTeam);
        BeforeText.Text =
            $"白班 = {currentDayTeam.Name}(班长 {currentDayTeam.LeaderName})\n" +
            $"夜班 = {currentNightTeam.Name}(班长 {currentNightTeam.LeaderName})";
        AfterText.Text =
            $"白班 = {currentNightTeam.Name}(班长 {currentNightTeam.LeaderName})\n" +
            $"夜班 = {currentDayTeam.Name}(班长 {currentDayTeam.LeaderName})";
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
