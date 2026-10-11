namespace ProductionDashboard.Core.Auth;

/// <summary>
/// 轻量班长鉴权:登录后 <see cref="IsAuthenticated"/> 为 true,可执行倒班 / 班组编辑等敏感操作。
/// 空闲超过 <see cref="AuthOptions.LeaderIdleTimeoutMinutes"/> 自动登出。
/// </summary>
public interface IAuthService
{
    /// <summary>当前登录的操作员姓名(LoginDialog 填写);未登录时为 null。</summary>
    string? CurrentOperator { get; }

    /// <summary>是否处于"班长登录态"。</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// 尝试登录。<paramref name="password"/> 与 <c>Display.LeaderPassword</c> 一致即成功。
    /// 成功返回 true 并触发 <see cref="AuthChanged"/>。
    /// 连续失败 5 次锁定 3 分钟(防暴力破解);成功/失败/锁定均写审计。
    /// </summary>
    bool TryLogin(string operatorName, string password);

    /// <summary>
    /// 校验退出大屏口令(与 <c>Display.ExitPassword</c> 比对)。统一走鉴权服务以便
    /// 写审计 + 失败锁定,不在窗口里裸比对。正确返回 true。连续失败 5 次锁定 3 分钟。
    /// </summary>
    bool VerifyExitPassword(string password);

    /// <summary>主动登出。无论当前是否已登录,都会把状态置为未登录并触发事件。</summary>
    void Logout();

    /// <summary>
    /// 通知"用户产生了一次活动",用于重置空闲超时计时。
    /// UI 上每次点击受保护按钮 / 鼠标进入受保护面板时调用。
    /// </summary>
    void NotifyActivity();

    /// <summary>登录态变化(login / logout / 空闲自动登出)。订阅方据此切换按钮可见性。</summary>
    event Action? AuthChanged;
}
