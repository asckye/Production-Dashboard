using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Plc;
using Sharp7;

namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// 真实 PLC 通信:使用 Sharp7 走 S7 协议轮询完成位 + 写心跳字。
/// 包含边沿检测、去抖、指数退避重连。
/// </summary>
public sealed class Sharp7PlcService : PlcServiceBase
{
    private readonly PlcOptions _options;
    private readonly ILogger<Sharp7PlcService> _logger;
    private readonly S7Address _signalAddr;
    private readonly S7Address? _heartbeatAddr;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private S7Client? _client;
    private readonly EdgeDetector _edge;
    private long? _lastHeartbeatAt;      // 单调时间戳,不受系统校时影响
    private short _heartbeatCounter;

    private volatile bool _connected;
    private bool _stopped;        // StopAsync 已跑过(可能多次,但只生效一次)
    private bool _disposed;       // DisposeAsync 幂等用

    /// <summary>构造 Edge 模式服务,在此校验完成位 / 心跳字地址类型。</summary>
    public Sharp7PlcService(IOptions<PlcOptions> options, ILogger<Sharp7PlcService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _edge = new EdgeDetector(TimeSpan.FromMilliseconds(_options.DebounceMs));
        _signalAddr = S7Address.Parse(_options.CompleteSignalAddress);
        _heartbeatAddr = string.IsNullOrWhiteSpace(_options.HeartbeatAddress)
            ? null
            : S7Address.Parse(_options.HeartbeatAddress!);

        if (_signalAddr.Kind != S7AreaKind.Bit)
        {
            throw new InvalidOperationException(
                $"CompleteSignalAddress 必须是位地址 (DBX),当前: {_options.CompleteSignalAddress}");
        }
        if (_heartbeatAddr is { Kind: not S7AreaKind.Word })
        {
            throw new InvalidOperationException(
                $"HeartbeatAddress 必须是字地址 (DBW),当前: {_options.HeartbeatAddress}");
        }
    }

    /// <inheritdoc/>
    protected override ILogger Logger => _logger;

    public override bool IsConnected => _connected;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 用 LongRunning 让运行时给一个专属 Thread 跑轮询循环,而不是占用 ThreadPool worker。
        // 理由:Sharp7 的 ConnectTo / DBRead 是同步阻塞调用 — PLC 不可达时可阻塞数十秒,
        // 期间这条线程完全冻结。如果它来自 ThreadPool,会挤占 worker 池;启动期多个 HostedService
        // 并发拉起 + DispatcherTimer 回调调度时,会触发 ThreadPool starvation 检测(500ms 后才补线程),
        // 表象就是 UI 首次刷新略卡顿。
        // StartNew + async lambda 返回 Task<Task>,需要 Unwrap() 拿到内层 task,
        // 这样 StopAsync 的 WaitAsync 才能等到真实的 PollLoopAsync 结束。
        _pollTask = Task.Factory.StartNew(
            () => PollLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 幂等:同一实例可能被 DI 注册成 IPlcService + IHostedService 两次,
        // Host 关闭流程会让 StopAsync / DisposeAsync 都走两遍。重复 Cancel 已 dispose
        // 的 CTS 会抛 ObjectDisposedException。
        if (_stopped) return;
        _stopped = true;

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* 已被 DisposeAsync 处理过,忽略 */ }

        try
        {
            if (_pollTask is not null)
            {
                // 关键:Sharp7 的 S7Client.ConnectTo() 是同步阻塞调用(PLC 不可达时
                // TCP 超时可达数十秒),内部 _cts.Cancel() 无法中断它。必须用
                // WaitAsync(cancellationToken) 配合 host 关闭超时,到点就放弃等待 —
                // 否则 App.OnExit 的 GetAwaiter().GetResult() 会把 UI 线程卡死,
                // 用户表象就是"关窗后进程仍驻留,任务管理器才能结束"。
                // 残留的 poll task 在 ThreadPool 后台线程上,不阻碍进程退出。
                try { await _pollTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* host 关闭超时或外部取消,放弃等待 */ }
                catch (Exception ex)
                {
                    // poll loop 异常 fault(NRE 之类的)在 WaitAsync 上会立刻重新抛出。
                    // 必须接住,否则 StopAsync 早退,下方 Disconnect 跳过 → S7Client 句柄泄露。
                    _logger.LogWarning(ex, "PLC 轮询任务异常退出,继续走断连流程");
                }
            }
        }
        finally
        {
            // 任何路径都要 Disconnect:无论 poll 正常结束、被取消、还是 fault,
            // 都要把 S7Client 的 socket 关掉,避免句柄泄露。
            Disconnect();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var pollDelay = TimeSpan.FromMilliseconds(_options.PollIntervalMs);
        var backoff = new ReconnectBackoff(TimeSpan.FromMilliseconds(_options.ReconnectMaxDelayMs));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_connected && !TryConnect())
                {
                    await Task.Delay(backoff.NextDelay(), ct).ConfigureAwait(false);
                    continue;
                }

                if (!TryReadSignalBit(out var bit))
                {
                    // 读失败:断开,同样等待退避间隔后再重连(不能立刻重连,见 ReconnectBackoff)
                    Disconnect();
                    await Task.Delay(backoff.NextDelay(), ct).ConfigureAwait(false);
                    continue;
                }

                backoff.Reset();
                if (_edge.Sample(bit)) RaiseSignal(DateTime.UtcNow);
                MaybeWriteHeartbeat();

                await Task.Delay(pollDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC 轮询异常,将断开重连");
                Disconnect();
                await Task.Delay(backoff.NextDelay(), ct).ConfigureAwait(false);
            }
        }
    }

    private bool TryConnect()
    {
        try
        {
            _client = new S7Client();
            var rc = _client.ConnectTo(_options.IpAddress, _options.Rack, _options.Slot);
            if (rc == 0)
            {
                _connected = true;
                _edge.Reset();   // 第一次读只对齐完成位状态,避免重连后误计一件
                _logger.LogInformation(
                    "PLC 已连接 · {Ip} rack={Rack} slot={Slot}",
                    _options.IpAddress, _options.Rack, _options.Slot);
                RaiseConnectionState(true);
                return true;
            }

            _logger.LogWarning(
                "PLC 连接失败 · {Ip} 错误={Code} {Msg}",
                _options.IpAddress, rc, _client.ErrorText(rc));
            _client.Disconnect();
            _client = null;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PLC 连接异常");
            _client = null;
            return false;
        }
    }

    private void Disconnect()
    {
        try { _client?.Disconnect(); } catch { /* ignore */ }
        _client = null;
        if (_connected)
        {
            _connected = false;
            RaiseConnectionState(false);
            _logger.LogWarning("PLC 已断开");
        }
    }

    private bool TryReadSignalBit(out bool bit)
    {
        bit = false;
        if (_client is null) return false;

        var buffer = new byte[1];
        var rc = _client.DBRead(_signalAddr.Db, _signalAddr.ByteOffset, 1, buffer);
        if (rc != 0)
        {
            _logger.LogWarning(
                "DBRead 失败 · DB{Db} byte={Byte} 错误={Code} {Msg}",
                _signalAddr.Db, _signalAddr.ByteOffset, rc, _client.ErrorText(rc));
            return false;
        }

        bit = S7.GetBitAt(buffer, 0, _signalAddr.BitOffset);
        return true;
    }

    private void MaybeWriteHeartbeat()
    {
        if (_heartbeatAddr is null || _client is null) return;

        var now = TimeProvider.System.GetTimestamp();
        if (_lastHeartbeatAt is { } last
            && TimeProvider.System.GetElapsedTime(last, now).TotalMilliseconds < _options.HeartbeatIntervalMs)
        {
            return;
        }

        var buffer = new byte[2];
        unchecked { _heartbeatCounter++; }
        S7.SetIntAt(buffer, 0, _heartbeatCounter);
        var rc = _client.DBWrite(_heartbeatAddr.Db, _heartbeatAddr.ByteOffset, 2, buffer);
        if (rc == 0)
        {
            _lastHeartbeatAt = now;
        }
        else
        {
            _logger.LogWarning(
                "心跳写入失败 · DB{Db} byte={Byte} 错误={Code}",
                _heartbeatAddr.Db, _heartbeatAddr.ByteOffset, rc);
        }
    }
}
