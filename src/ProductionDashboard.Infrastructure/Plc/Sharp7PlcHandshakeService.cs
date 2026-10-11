using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharp7;

namespace ProductionDashboard.Infrastructure.Plc;

/// <summary>
/// 握手协议版 PLC 通信(<see cref="PlcSampleMode.Handshake"/>):
/// 周期性读 1 位 + 1 双字(<c>CompleteBit</c> + <c>CompleteSeq</c>),通过
/// <see cref="HandshakeDecoder"/> 判定是否有新事件,有则计数并把 bit 写 0 应答 PLC。
/// 相比 <see cref="Sharp7PlcService"/>:
/// <list type="bullet">
///   <item>零漏读:即使一次轮询窗口跨多个 PLC 触发,通过 seq 差值都能算出 delta</item>
///   <item>无需去抖:序号变化即新事件,无虚假抖动</item>
///   <item>崩溃可重入:重启后 <c>_isFirstRead=true</c>,只对齐 lastSeq 不重计;
///         后续读到 <c>bit=1</c> 但 <c>seq</c> 没变 → 只清位不重计</item>
///   <item>断线期间 PLC 累计触发的事件全部会在重连后通过 seq 跳变识别</item>
/// </list>
/// PLC 端需配合添加一个 DWORD 双字(默认 <c>DB100.DBD2</c>),写法:
/// <code>
/// IF 检测到完成 AND DB100.DBX0.0 = FALSE THEN
///     IF DB100.DBD2 >= 4294967295 THEN
///         DB100.DBD2 := 0;            // 显式回卷, 不依赖 CPU 隐式溢出
///     ELSE
///         DB100.DBD2 := DB100.DBD2 + 1;
///     END_IF;
///     DB100.DBX0.0 := TRUE;
/// END_IF;
/// </code>
/// </summary>
public sealed class Sharp7PlcHandshakeService : PlcServiceBase
{
    private readonly PlcOptions _options;
    private readonly ILogger<Sharp7PlcHandshakeService> _logger;
    private readonly S7Address _signalAddr;
    private readonly S7Address _seqAddr;
    private readonly S7Address? _heartbeatAddr;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private S7Client? _client;
    private bool _isFirstRead = true;
    private uint _lastSeq;
    private long? _lastHeartbeatAt;      // 单调时间戳,不受系统校时影响
    private short _heartbeatCounter;

    private volatile bool _connected;
    private bool _stopped;
    private bool _disposed;

    /// <summary>构造 Handshake 模式服务,在此校验配置(地址必须有效 + 在同一 DB 块)。</summary>
    public Sharp7PlcHandshakeService(
        IOptions<PlcOptions> options,
        ILogger<Sharp7PlcHandshakeService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _signalAddr = S7Address.Parse(_options.CompleteSignalAddress);
        if (_signalAddr.Kind != S7AreaKind.Bit)
        {
            throw new InvalidOperationException(
                $"Handshake 模式下 CompleteSignalAddress 必须是位地址 (DBX),当前: {_options.CompleteSignalAddress}");
        }

        if (string.IsNullOrWhiteSpace(_options.CompleteSeqAddress))
        {
            throw new InvalidOperationException(
                "Handshake 模式下 CompleteSeqAddress 不能为空,需配 PLC 端序号双字地址,例 DB100.DBD2。");
        }
        _seqAddr = S7Address.Parse(_options.CompleteSeqAddress);
        if (_seqAddr.Kind != S7AreaKind.DWord)
        {
            throw new InvalidOperationException(
                $"Handshake 模式下 CompleteSeqAddress 必须是双字地址 (DBD),当前: {_options.CompleteSeqAddress}");
        }
        if (_seqAddr.Db != _signalAddr.Db)
        {
            throw new InvalidOperationException(
                $"CompleteSignalAddress 与 CompleteSeqAddress 必须在同一 DB 块(当前 DB{_signalAddr.Db} vs DB{_seqAddr.Db}),否则无法在单次读中拿到 bit+seq 的一致快照。");
        }

        _heartbeatAddr = string.IsNullOrWhiteSpace(_options.HeartbeatAddress)
            ? null
            : S7Address.Parse(_options.HeartbeatAddress!);
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
        // 同 Sharp7PlcService:用 LongRunning 跑专属 Thread,避免同步阻塞 IO 占 ThreadPool worker。
        // Sharp7 的 ConnectTo / DBRead / DBWrite 全是同步阻塞,PLC 不可达时单次调用可数十秒不归。
        _pollTask = Task.Factory.StartNew(
            () => PollLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* 已被 DisposeAsync 处理过 */ }

        try
        {
            if (_pollTask is not null)
            {
                // 同 Sharp7PlcService:S7Client.ConnectTo 同步阻塞,WaitAsync 给 host 关闭限时
                try { await _pollTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* host 关闭超时,放弃等待 */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PLC 握手轮询任务异常退出,继续走断连流程");
                }
            }
        }
        finally
        {
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

                if (!TryHandshakeOnce())
                {
                    // 读失败:断开,同样等待退避间隔后再重连(不能立刻重连,见 ReconnectBackoff)
                    Disconnect();
                    await Task.Delay(backoff.NextDelay(), ct).ConfigureAwait(false);
                    continue;
                }

                backoff.Reset();
                MaybeWriteHeartbeat();

                await Task.Delay(pollDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC 握手轮询异常,将断开重连");
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
                // 重连后沿用断线前的 lastSeq,不重新对齐:PLC 的序号在断线期间照常累加,
                // 重连后第一次读就能按序号差把断线期间的件数补上(握手模式"零漏读"靠的就是这一点)。
                // 只有程序刚启动时(_isFirstRead 初始为 true)才只对齐、不计数。
                // PLC 重启导致序号归零的情况由 HandshakeDecoder 识别并重新对齐。
                _logger.LogInformation(
                    "PLC 握手连接成功 · {Ip} rack={Rack} slot={Slot}",
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

    /// <summary>
    /// 单次握手读 + 处理。读 1 个 bit + 1 个 dword(同一 DB 块,一次 DBRead 足够覆盖到两个偏移)。
    /// 失败返回 false,调用方应触发断连重连。
    /// </summary>
    private bool TryHandshakeOnce()
    {
        if (_client is null) return false;

        // 一次 DBRead 把覆盖 bit 偏移 + dword 偏移范围的所有字节都拿到。
        // bit 偏移 _signalAddr.ByteOffset,占 1 字节;dword 偏移 _seqAddr.ByteOffset,占 4 字节。
        var bitByte = _signalAddr.ByteOffset;
        var seqByte = _seqAddr.ByteOffset;
        var seqSize = _seqAddr.ByteSize;                // DWord = 4
        var startByte = Math.Min(bitByte, seqByte);
        var endByte = Math.Max(bitByte, seqByte + seqSize - 1);
        var length = endByte - startByte + 1;
        var buffer = new byte[length];

        var rc = _client.DBRead(_signalAddr.Db, startByte, length, buffer);
        if (rc != 0)
        {
            _logger.LogWarning(
                "握手 DBRead 失败 · DB{Db} 起始字节={Byte} 长度={Len} 错误={Code} {Msg}",
                _signalAddr.Db, startByte, length, rc, _client.ErrorText(rc));
            return false;
        }

        var bit = S7.GetBitAt(buffer, bitByte - startByte, _signalAddr.BitOffset);
        var seqOffset = seqByte - startByte;
        // S7.GetDIntAt 返回 int(有符号 32);我们语义上想要无符号 DWORD,直接 cast。
        var seq = unchecked((uint)S7.GetDIntAt(buffer, seqOffset));

        if (!_isFirstRead && bit && HandshakeDecoder.IsCounterReset(seq, _lastSeq))
        {
            _logger.LogWarning(
                "PLC 序号跳变 {Last} → {Seq},超出合理范围,视为 PLC 计数器被复位(重启 / 手动改写),重新对齐,本次不计数",
                _lastSeq, seq);
        }

        var (delta, shouldClear, newLastSeq) =
            HandshakeDecoder.Decode(bit, seq, _isFirstRead, _lastSeq);

        if (_isFirstRead)
        {
            _logger.LogInformation(
                "PLC 握手首次读对齐 · seq={Seq} bit={Bit}", seq, bit);
            _isFirstRead = false;
        }
        else if (delta > 1)
        {
            _logger.LogInformation("PLC 握手一次读到 {Delta} 件(轮询间隔内多次完成或断线期间累计)", delta);
        }

        for (var i = 0; i < delta; i++)
        {
            RaiseSignal(DateTime.UtcNow);
        }
        _lastSeq = newLastSeq;

        if (shouldClear)
        {
            // 把 bit 字节(只改 bit 偏移那一位)写回 0。
            // 简化处理:先重新读这一字节,改 bit,写回。比直接写 0 整字节安全(防意外清掉相邻 bit)。
            var bitClearBuffer = new byte[1];
            var rcRead = _client.DBRead(_signalAddr.Db, bitByte, 1, bitClearBuffer);
            if (rcRead != 0)
            {
                _logger.LogWarning(
                    "清位前重读失败 · DB{Db} byte={Byte} 错误={Code}",
                    _signalAddr.Db, bitByte, rcRead);
                return false;
            }
            S7.SetBitAt(bitClearBuffer, 0, _signalAddr.BitOffset, false);
            var rcWrite = _client.DBWrite(_signalAddr.Db, bitByte, 1, bitClearBuffer);
            if (rcWrite != 0)
            {
                _logger.LogWarning(
                    "清位写入失败 · DB{Db} byte={Byte} 错误={Code} {Msg}",
                    _signalAddr.Db, bitByte, rcWrite, _client.ErrorText(rcWrite));
                // 不直接返回 false — 写位失败但读到的事件已经 RaiseSignal,
                // 下次轮询 bit 还是 1 但 seq 没变 → decoder 会再次发出 ShouldClear,自然重试。
            }
        }

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
