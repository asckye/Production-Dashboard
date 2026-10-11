namespace ProductionDashboard.Core.Diagnostics;

/// <summary>
/// 全局异常汇集点。AppDomain / Dispatcher / TaskScheduler 三条逃逸路径都汇到这里,
/// 集中写日志 + 抛事件给 UI 显示红条告警。
/// </summary>
public interface IGlobalExceptionHandler
{
    /// <summary>
    /// 有异常被报告时触发。订阅方(通常是 <c>MainViewModel</c>)负责把它 marshal 到 Dispatcher。
    /// </summary>
    event EventHandler<ExceptionReportedEventArgs>? ExceptionReported;

    /// <summary>记录并广播一个异常。来源标识用于区分捕获路径。</summary>
    void Report(Exception exception, string source);
}

/// <summary>
/// 异常报告事件参数。<see cref="Exception"/> 保留完整对象以便日志拿到 stack trace,
/// 但 UI 只显示 <see cref="Message"/>(避免在大屏上糊一坨 stack)。
/// </summary>
public sealed class ExceptionReportedEventArgs : EventArgs
{
    /// <summary>异常来源标识(AppDomain / Dispatcher / TaskScheduler / 业务模块名)。</summary>
    public required string Source { get; init; }
    /// <summary>给 UI 显示的简短消息(取 Exception.Message,不含 stack)。</summary>
    public required string Message { get; init; }
    /// <summary>异常发生时间戳。UI 在 toast 标题中显示 HH:mm:ss。</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>完整异常对象,日志层用于打印 stack。UI 不直接呈现。</summary>
    public required Exception Exception { get; init; }
}
