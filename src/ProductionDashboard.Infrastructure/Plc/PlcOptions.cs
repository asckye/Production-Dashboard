using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// PLC 通信配置。所有字段在 <c>appsettings.json</c> 的 <c>Plc</c> 节点下绑定。
/// </summary>
public sealed class PlcOptions : IValidatableObject
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Plc" 段。</summary>
    public const string SectionName = "Plc";

    /// <summary>PLC IP 地址 (IPv4)。</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^(\d{1,3}\.){3}\d{1,3}$",
        ErrorMessage = "IpAddress must be an IPv4 address (e.g. 192.168.0.10).")]
    public string IpAddress { get; init; } = "127.0.0.1";

    /// <summary>S7 机架号 (Rack)。</summary>
    [Range(0, 7)]
    public int Rack { get; init; } = 0;

    /// <summary>S7 槽号 (Slot)。</summary>
    [Range(0, 31)]
    public int Slot { get; init; } = 1;

    /// <summary>"完成信号" 位地址,例如 <c>DB100.DBX0.0</c>。</summary>
    [Required(AllowEmptyStrings = false)]
    public string CompleteSignalAddress { get; init; } = "DB100.DBX0.0";

    /// <summary>
    /// 采样模式。默认 <see cref="PlcSampleMode.Edge"/>(上升沿+去抖,向后兼容)。
    /// 切到 <see cref="PlcSampleMode.Handshake"/> 后必须同时配
    /// <see cref="CompleteSeqAddress"/>,且 PLC 端需要新增对应 DWORD 序号字。
    /// </summary>
    public PlcSampleMode SampleMode { get; init; } = PlcSampleMode.Edge;

    /// <summary>
    /// 握手模式下「完成序号」双字地址,例如 <c>DB100.DBD2</c>。Handshake 模式必填。
    /// PLC 端每次完成事件触发时,先 +1 这个序号、再置位 <see cref="CompleteSignalAddress"/>;
    /// 上位机识别新序号后清位,实现零漏读握手。
    /// <para>
    /// 类型为 DWORD (32 位无符号, 0..4294967295),足够整个设备生命周期不溢出;
    /// 即使溢出,上位机解码器用模 2^32 算 delta,自然回卷。
    /// PLC 端应显式判断 <c>IF Seq &gt;= 4294967295 THEN Seq := 0 ELSE Seq + 1</c>,
    /// 不要依赖 PLC 隐式溢出行为(各品牌不一致)。
    /// </para>
    /// </summary>
    [RegularExpression(@"^DB\d+\.DBD\d+$",
        ErrorMessage = "CompleteSeqAddress must be a DBD address (e.g. DB100.DBD2).")]
    public string? CompleteSeqAddress { get; init; }

    /// <summary>心跳字地址(可选),例如 <c>DB100.DBW2</c>。留空则不写心跳。</summary>
    public string? HeartbeatAddress { get; init; }

    /// <summary>PLC 轮询间隔 (ms),建议 50-200ms。</summary>
    [Range(20, 5000)]
    public int PollIntervalMs { get; init; } = 100;

    /// <summary>信号去抖窗口 (ms),同一信号在窗口内只算一次。</summary>
    [Range(0, 5000)]
    public int DebounceMs { get; init; } = 200;

    /// <summary>断线重连指数退避上限 (ms)。</summary>
    [Range(1000, 60000)]
    public int ReconnectMaxDelayMs { get; init; } = 30000;

    /// <summary>心跳写入间隔 (ms)。</summary>
    [Range(1000, 60000)]
    public int HeartbeatIntervalMs { get; init; } = 5000;

    /// <summary>
    /// 跨字段校验:Handshake 模式必须配 <see cref="CompleteSeqAddress"/>。
    /// 单字段的 <c>[Required]</c> 表达不了"依赖 SampleMode"的条件必填,放这里在 ValidateOnStart 期即拦下。
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SampleMode == PlcSampleMode.Handshake && string.IsNullOrWhiteSpace(CompleteSeqAddress))
        {
            yield return new ValidationResult(
                "Handshake 模式下 CompleteSeqAddress 必填(PLC 端序号双字地址,例 DB100.DBD2)。",
                [nameof(CompleteSeqAddress)]);
        }
    }
}
