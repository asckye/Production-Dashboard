using System.Windows;
using System.Windows.Input;
using ProductionDashboard.Core.Auth;

namespace ProductionDashboard.Views;

/// <summary>
/// 班长登录弹窗。验证 <see cref="IAuthService.TryLogin"/>;
/// 通过则 DialogResult=true,调用方可继续敏感操作。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly IAuthService _auth;

    /// <summary>注入 <see cref="IAuthService"/>。窗口 Loaded 后焦点落到操作员输入框。</summary>
    public LoginWindow(IAuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Loaded += (_, _) => OperatorBox.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Validate();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnAnyKeyDown(object sender, KeyEventArgs e)
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
        var operatorName = OperatorBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            ShowError("请输入操作员姓名");
            OperatorBox.Focus();
            return;
        }
        if (string.IsNullOrEmpty(PwdBox.Password))
        {
            ShowError("请输入口令");
            PwdBox.Focus();
            return;
        }

        if (_auth.TryLogin(operatorName, PwdBox.Password))
        {
            DialogResult = true;
            Close();
        }
        else
        {
            ShowError("操作员姓名 / 口令错误");
            PwdBox.Clear();
            PwdBox.Focus();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
