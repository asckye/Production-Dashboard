using System.Windows;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Views;

namespace ProductionDashboard.Services.UI;

/// <summary><see cref="IDialogService"/> 的 WPF 实现:以主窗口为 Owner 弹出模态对话框。</summary>
public sealed class DialogService : IDialogService
{
    private readonly IAuthService _auth;

    /// <summary>注入登录服务(登录对话框需要用它校验口令)。</summary>
    public DialogService(IAuthService auth)
    {
        _auth = auth;
    }

    /// <inheritdoc/>
    public bool ShowLogin()
    {
        var dialog = new LoginWindow(_auth) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    /// <inheritdoc/>
    public bool ConfirmShiftSwap(Team currentDayTeam, Team currentNightTeam)
    {
        var dialog = new SwapConfirmationWindow(currentDayTeam, currentNightTeam)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }
}
