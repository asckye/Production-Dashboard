using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Plc;

namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// PLC 服务抽象基类。同时实现 <see cref="IPlcService"/> 与 <see cref="IHostedService"/>,
/// 这样可以注册一次单例,Host 启动期自动调起 <c>StartAsync</c>。
/// </summary>
public abstract class PlcServiceBase : IPlcService, IHostedService, IAsyncDisposable
{
    /// <summary>
    /// 派生类提供的日志器。基类用它记录"订阅方抛异常"——这种异常发生在 PLC 采集线程上,
    /// 必须有日志,否则计数会静默停止而 PLC 仍显示已连接,无法诊断。
    /// </summary>
    protected abstract ILogger Logger { get; }

    /// <inheritdoc/>
    public abstract bool IsConnected { get; }

    /// <inheritdoc/>
    public event Action<DateTime>? CompleteSignalReceived;

    /// <inheritdoc/>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>由 Host 启动期调用 — 派生类在此连接 PLC 并启动轮询循环。</summary>
    public abstract Task StartAsync(CancellationToken cancellationToken);

    /// <summary>由 Host 关闭期调用 — 派生类在此优雅断开 PLC 并停止轮询。</summary>
    public abstract Task StopAsync(CancellationToken cancellationToken);

    /// <summary>异步释放资源。默认空实现,派生类按需重写。</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>触发"完成"信号事件。</summary>
    protected void RaiseSignal(DateTime utcNow)
    {
        // 订阅方异常不能影响采集线程(否则一次抛错就停掉整个轮询循环),
        // 但绝不能静默吞掉 —— 这是产品计数路径上最关键的异常点,
        // 订阅方(CountingService.OnSignal)若抛 NRE 等异常会导致本次信号丢失,必须留痕。
        try { CompleteSignalReceived?.Invoke(utcNow); }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "CompleteSignalReceived 订阅方抛出异常,本次完成信号可能未被计数 (utcNow={UtcNow:O})", utcNow);
        }
    }

    /// <summary>触发连接状态变化事件。</summary>
    protected void RaiseConnectionState(bool isConnected)
    {
        try { ConnectionStateChanged?.Invoke(isConnected); }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "ConnectionStateChanged 订阅方抛出异常 (isConnected={IsConnected})", isConnected);
        }
    }
}
