namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// PLC 握手协议的状态机解码 — 纯函数,完全无 IO,易测易复用。
/// <para>
/// 握手协议(<see cref="PlcSampleMode.Handshake"/>):
/// </para>
/// <list type="number">
///   <item>PLC 检测到完成事件 → <c>CompleteSeq + 1</c>(DWORD, 满则显式回卷到 0)→ 置位 <c>CompleteBit = 1</c></item>
///   <item>上位机周期轮询读位 + 双字</item>
///   <item>识别新序号(<c>seq != lastSeq</c> 且 <c>bit=1</c>)→ 计数 + 清位</item>
///   <item>程序启动后的第一次读只对齐 lastSeq,不计数;断线重连后沿用原 lastSeq,把断线期间的件数补上</item>
///   <item>一次增量超过 <see cref="MaxPlausibleDelta"/> 视为 PLC 计数器被复位,重新对齐、不计数</item>
///   <item>读到 <c>bit=1</c> 但 <c>seq == lastSeq</c> → 上次清位失败 / 上位机崩过 → 只清位不重计</item>
/// </list>
/// <para>
/// 序号字段是 DWORD(32 位无符号,0..4294967295),回卷由模 2^32 算 delta 处理,跨回卷透明。
/// 实际工业场景下设备生命周期内基本不会真正发生回卷(1000Hz 触发也要 ~50 天)。
/// </para>
/// </summary>
public static class HandshakeDecoder
{
    private const long Modulus = 1L << 32;  // 2^32 = 4294967296

    /// <summary>
    /// 一次读到的合理增量上限。PLC 重启后序号从 0 重新开始,按模 2^32 计算会得到接近 43 亿的"增量",
    /// 照算会瞬间计入几十亿件;超过这个值就认为计数器被复位,重新对齐。
    /// 1 万件对应每秒 1 件的产线断线近 3 小时,足够覆盖正常的断线补录。
    /// </summary>
    public const int MaxPlausibleDelta = 10_000;

    /// <summary>从 <paramref name="lastSeq"/> 到 <paramref name="seq"/> 的增量,按 DWORD 回卷取模。</summary>
    public static long Distance(uint seq, uint lastSeq) => ((long)seq - lastSeq + Modulus) % Modulus;

    /// <summary>序号变化超出合理范围,视为 PLC 计数器被复位。</summary>
    public static bool IsCounterReset(uint seq, uint lastSeq) => Distance(seq, lastSeq) > MaxPlausibleDelta;

    /// <summary>
    /// 解码一次握手读结果。
    /// </summary>
    /// <param name="bit">当前 CompleteBit 值。</param>
    /// <param name="seq">当前 CompleteSeq 值(DWORD,0..4294967295)。</param>
    /// <param name="isFirstRead">true 表示程序启动后的第一次读。</param>
    /// <param name="lastSeq">上一次记录的序号(<paramref name="isFirstRead"/>=true 时忽略)。</param>
    /// <returns>
    /// <para><c>Delta</c>:本次应触发多少次 RaiseSignal(0 = 没有新事件)。</para>
    /// <para><c>ShouldClearBit</c>:本次轮询完成后是否要把 CompleteBit 写 0(向 PLC 应答)。</para>
    /// <para><c>NewLastSeq</c>:本次结束后 lastSeq 应更新为这个值。</para>
    /// </returns>
    public static (int Delta, bool ShouldClearBit, uint NewLastSeq) Decode(
        bool bit, uint seq, bool isFirstRead, uint lastSeq)
    {
        if (isFirstRead)
        {
            // 程序启动后第一次:不计数,只对齐 lastSeq。
            // 不清位:让 PLC 端的状态(可能 bit=1)由下一次轮询正常处理。
            return (Delta: 0, ShouldClearBit: false, NewLastSeq: seq);
        }
        if (!bit)
        {
            // 位 = 0,无待处理事件。lastSeq 保持。
            return (Delta: 0, ShouldClearBit: false, NewLastSeq: lastSeq);
        }
        if (seq == lastSeq)
        {
            // 位 = 1 但序号未变 → 上次写 0 失败 / 上位机崩过后重启。
            // 只清位,不重复计数(避免崩溃重启时多计一次)。
            return (Delta: 0, ShouldClearBit: true, NewLastSeq: lastSeq);
        }
        // 位 = 1 且序号变了 → 新事件(理论上 PLC 单次握手 delta=1,
        // 但若上位机一次轮询窗口内 PLC 触发多次,或断线期间累计,delta 可能 > 1)。
        // DWORD 回卷:取模计算,跨回卷透明。
        var distance = Distance(seq, lastSeq);
        if (distance > MaxPlausibleDelta)
        {
            // 计数器被复位:重新对齐并清位,不计数(宁可少计,也不能一次计入几十亿件)
            return (Delta: 0, ShouldClearBit: true, NewLastSeq: seq);
        }
        return (Delta: (int)distance, ShouldClearBit: true, NewLastSeq: seq);
    }
}
