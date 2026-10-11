namespace ProductionDashboard.Core.Plc;

/// <summary>
/// 上位机视角的 PLC 通信接口。具体实现是 Sharp7 真实 S7 通信,按
/// 配置项 <c>Plc:SampleMode</c> 决定走 Edge 还是 Handshake 路径。
/// 同一实例同时也注册为 <c>IHostedService</c>,启动期自动连接 / 退出时自动断开。
/// </summary>
public interface IPlcService
{
    /// <summary>当前是否处于连接状态。</summary>
    bool IsConnected { get; }

    /// <summary>PLC 报告"完成一件"时触发。事件参数是信号到达本机的时刻 (UTC)。</summary>
    event Action<DateTime>? CompleteSignalReceived;

    /// <summary>连接状态变化时触发,参数 true=已连接, false=断开。</summary>
    event Action<bool>? ConnectionStateChanged;
}
