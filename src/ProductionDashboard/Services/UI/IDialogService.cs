using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Services.UI;

/// <summary>
/// 弹出对话框的统一入口。ViewModel 通过它请求对话框,而不是自己 new 窗口,
/// 这样 ViewModel 不依赖具体窗口类,也方便在测试里替换成假实现。
/// </summary>
public interface IDialogService
{
    /// <summary>弹出班长登录对话框。登录成功返回 true,取消返回 false。</summary>
    bool ShowLogin();

    /// <summary>弹出倒班确认对话框,展示对调前后的班组。用户确认返回 true。</summary>
    bool ConfirmShiftSwap(Team currentDayTeam, Team currentNightTeam);
}
