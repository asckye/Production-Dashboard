using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Core.Auth;

/// <summary>
/// 登录与退出口令配置。
/// <para>
/// 与界面配置共用 appsettings.json 的 <c>Display</c> 节点(历史配置键保持不变,
/// 已部署的 <c>Display:ExitPassword</c> / <c>Display:LeaderPassword</c> 无需修改)。
/// 生产环境请用环境变量 <c>Display__ExitPassword</c> / <c>Display__LeaderPassword</c> 设置强口令。
/// </para>
/// </summary>
public sealed class AuthOptions
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Display" 段。</summary>
    public const string SectionName = "Display";

    /// <summary>Esc 退出全屏所需的密码。最低 4 位以防止误操作。</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(4)]
    public string ExitPassword { get; init; } = "1234";

    /// <summary>
    /// 班长登录口令(用于倒班切换 / 班组管理等敏感操作)。
    /// 部署时请改为强口令,与 <see cref="ExitPassword"/> 区分。
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(4)]
    public string LeaderPassword { get; init; } = "leader";

    /// <summary>班长登录态空闲多少分钟后自动登出。范围 1-240,默认 5。</summary>
    [Range(1, 240)]
    public int LeaderIdleTimeoutMinutes { get; init; } = 5;
}
