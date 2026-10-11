using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Infrastructure.Maintenance;

/// <summary>系统健康监控配置。绑定 <c>SystemHealth</c> 节点。</summary>
public sealed class SystemHealthOptions
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "SystemHealth" 段。</summary>
    public const string SectionName = "SystemHealth";

    /// <summary>总开关。false 时不起后台巡检任务,适合开发调试或低配机器。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>巡检间隔(毫秒)。</summary>
    [Range(10_000, 600_000)]
    public int CheckIntervalMs { get; init; } = 60_000;

    /// <summary>
    /// 时间倒退判定阈值(秒)。
    /// 两次巡检之间系统时间倒退超过这个值,认为时钟被改了 / NTP 跳变。
    /// </summary>
    [Range(5, 600)]
    public int MaxClockRegressionSeconds { get; init; } = 30;

    /// <summary>
    /// 数据库所在磁盘的最低剩余空间(MB),低于即告警。
    /// </summary>
    [Range(100, 100_000)]
    public int MinFreeDiskMb { get; init; } = 1024;
}
