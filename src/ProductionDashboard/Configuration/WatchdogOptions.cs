using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Configuration;

/// <summary>
/// 看门狗配置。绑定 <c>Watchdog</c> 节点。
/// </summary>
public sealed class WatchdogOptions : IValidatableObject
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Watchdog" 段。</summary>
    public const string SectionName = "Watchdog";

    /// <summary>
    /// 是否启用看门狗。开发期一般关掉,正式部署开启。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 心跳文件路径,相对路径会以应用基目录解析。
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string HeartbeatPath { get; init; } = "runtime/heartbeat.json";

    /// <summary>
    /// UI 线程写心跳的间隔 (毫秒)。
    /// </summary>
    [Range(1000, 60_000)]
    public int HeartbeatIntervalMs { get; init; } = 5000;

    /// <summary>
    /// 心跳超时多少毫秒后判定主程序失活。
    /// 必须 ≥ 心跳间隔的 3 倍,留出 GC / IO 抖动空间。
    /// </summary>
    [Range(15_000, 600_000)]
    public int HeartbeatTimeoutMs { get; init; } = 60_000;

    /// <summary>
    /// 守护进程轮询心跳的间隔 (毫秒)。
    /// </summary>
    [Range(1000, 60_000)]
    public int PollIntervalMs { get; init; } = 5000;

    /// <summary>
    /// 杀进程后等待多少毫秒再重启,给单实例锁 / 文件句柄释放留时间。
    /// </summary>
    [Range(500, 30_000)]
    public int RestartDelayMs { get; init; } = 3000;

    /// <summary>
    /// 跨字段校验:心跳超时必须 ≥ 3 × 心跳间隔(留出 GC / IO 抖动空间,否则正常卡顿就误判失活、误重启)。
    /// 单字段 <c>[Range]</c> 只能管绝对下界,这个相对关系放这里在 ValidateOnStart 期即拦下。
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HeartbeatTimeoutMs < HeartbeatIntervalMs * 3)
        {
            yield return new ValidationResult(
                $"HeartbeatTimeoutMs ({HeartbeatTimeoutMs}) 必须 ≥ 3 × HeartbeatIntervalMs ({HeartbeatIntervalMs}),否则正常抖动会误判主程序失活并重启。",
                [nameof(HeartbeatTimeoutMs), nameof(HeartbeatIntervalMs)]);
        }
    }
}
