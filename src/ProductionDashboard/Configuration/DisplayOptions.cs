using System.ComponentModel.DataAnnotations;
using ProductionDashboard.Core.Auth;

namespace ProductionDashboard.Configuration;

/// <summary>
/// 大屏显示配置。绑定 <c>Display</c> 节点。
/// 同一节点下的口令与登录超时由 Core 层的 <c>AuthOptions</c> 读取。
/// </summary>
public sealed class DisplayOptions
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Display" 段。</summary>
    public const string SectionName = "Display";

    /// <summary>是否启动即全屏 (无边框 + 占满主屏)。</summary>
    public bool Fullscreen { get; init; } = true;

    /// <summary>顶栏显示的工厂/产线名称(默认:示例车间 | 1 号产线,部署时按现场修改)。</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(64)]
    public string FactoryName { get; init; } = "示例车间 | 1 号产线";

    /// <summary>
    /// 鼠标静止多少毫秒后自动隐藏(仅全屏模式生效,窗口模式永不隐藏)。
    /// 默认 10 秒,操作员碰一下又能看到。
    /// </summary>
    [Range(500, 60_000)]
    public int CursorHideAfterMs { get; init; } = 10_000;
}
