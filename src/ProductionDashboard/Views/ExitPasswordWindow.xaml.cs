using System.Windows;
using System.Windows.Input;
using ProductionDashboard.Core.Auth;

namespace ProductionDashboard.Views;

/// <summary>退出大屏的密码确认弹窗。返回 <c>true</c> 表示密码正确。</summary>
public partial class ExitPasswordWindow : Window
{
    private readonly IAuthService _auth;

    /// <summary>注入鉴权服务 — 校验、审计、失败锁定统一在 <see cref="IAuthService.VerifyExitPassword"/> 内,窗口不持有明文口令。</summary>
    public ExitPasswordWindow(IAuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Loaded += (_, _) => PwdBox.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Validate();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        // 标记已处理:否则 IsDefault 的确认按钮会再响应同一次回车,一次输错被记成两次失败
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Validate();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }

    private void Validate()
    {
        if (_auth.VerifyExitPassword(PwdBox.Password))
        {
            DialogResult = true;
            Close();
        }
        else
        {
            ErrorText.Text = "密码错误";
            ErrorText.Visibility = Visibility.Visible;
            PwdBox.Clear();
            PwdBox.Focus();
        }
    }
}
