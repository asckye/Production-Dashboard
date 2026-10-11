using System.Net.Sockets;

namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// 判定 <see cref="TaskScheduler.UnobservedTaskException"/> 抓到的异常,
/// 是否属于已知第三方库泄露出来的"良性"未观察 Task,可以静默吞掉。
///
/// <para>
/// 当前覆盖:Sharp7 1.1.84 的 <c>MsgSocket.TCPPing</c> 在 PLC 不可达时,
/// 用 <c>BeginConnect + AsyncWaitHandle.WaitOne(timeout)</c> 等超时后直接
/// <c>socket.Close()</c>;此举把那个未完成的 <c>BeginConnect</c> 异步 Task
/// 内部以 <see cref="SocketError.OperationAborted"/> 故障掉,但 Sharp7
/// 从不调 <c>EndConnect</c> → Task 永远不会被 observe → GC 时
/// 落到 <c>UnobservedTaskException</c>。
/// </para>
///
/// <para>
/// Sharp7 已经通过返回值 <c>LastError=3 (errTCPConnectionFailed)</c> 把超时
/// 正确处理给业务层,UI 上 <c>PlcConnected</c> 徽章会反映真实状态。这条 Task
/// 异常对功能无害,只是 .NET 把它捞起来塞进了全局 handler,会被 UI 当成
/// 系统级故障弹红条 → 操作员误以为系统出故障,实际只是 PLC 没连上。
/// </para>
/// </summary>
public static class BenignTaskExceptionFilter
{
    /// <summary>
    /// 检查异常(含其内部嵌套结构)是否完全由"socket 操作中止"组成。
    /// 严格条件:<see cref="AggregateException"/> 内的所有叶子必须都判定为良性,
    /// 只要混入一个其他类型,就视为真异常,不静默。
    /// </summary>
    public static bool IsBenignSocketAbort(Exception? exception)
    {
        if (exception is null) return false;

        // Task.Exception 总是 AggregateException — 把它的所有叶子摊平,要求每一个都是良性 socket abort
        if (exception is AggregateException agg)
        {
            var inners = agg.Flatten().InnerExceptions;
            if (inners.Count == 0) return false;
            foreach (var inner in inners)
            {
                if (!IsLeafBenign(inner)) return false;
            }
            return true;
        }

        return IsLeafBenign(exception);
    }

    private static bool IsLeafBenign(Exception leaf)
    {
        // 顺着 InnerException 链查 — 包装(例如 IOException(SocketException))也算
        for (var current = leaf; current is not null; current = current.InnerException)
        {
            if (current is SocketException se && IsBenignSocketError(se.SocketErrorCode))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsBenignSocketError(SocketError code) =>
        code is SocketError.OperationAborted   // WSA_OPERATION_ABORTED (995):socket 被关掉时进行中的异步 I/O 中止
            or SocketError.Interrupted;        // WSAEINTR (10004):同源的旧式表达,有些 .NET 路径会用这个
}
