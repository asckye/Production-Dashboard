namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// PLC 采样模式 — 决定上位机如何从 PLC 读取「完成」信号。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Edge"/> 是历史上使用的模式:轮询位状态 + 上升沿检测 + 200ms 去抖。
/// 工作但有采样定理性漏读风险(PLC 扫描 20ms vs 上位机轮询 100ms,
/// 若完成位只保持 1 个 PLC 扫描周期就清零,上位机可能在两次轮询之间错过)。
/// </para>
/// <para>
/// <see cref="Handshake"/> 是改进模式:PLC 端置位并写序号字,上位机识别新序号后
/// 先持久化事件、再清位。零漏读,可重入,不需要去抖。需要 PLC 端配合加一个 DWORD 序号字。
/// </para>
/// </remarks>
public enum PlcSampleMode
{
    /// <summary>
    /// 上升沿检测 + 去抖(默认,向后兼容)。
    /// 走 <c>Sharp7PlcService</c> 实现。
    /// </summary>
    Edge = 0,

    /// <summary>
    /// 握手位 + 序号字:PLC 置位 + 写 seq,上位机识别新 seq 后清位。
    /// 走 <c>Sharp7PlcHandshakeService</c> 实现。
    /// 需要 <see cref="PlcOptions.CompleteSeqAddress"/> 配置 PLC 端序号字地址。
    /// </summary>
    Handshake = 1,
}
