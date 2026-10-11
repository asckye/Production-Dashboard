using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Plc;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Production;

/// <summary>
/// 把 PLC 信号、班次状态、班组分配、SQLite 落库串起来的服务:
/// <list type="bullet">
///   <item>订阅 <see cref="IPlcService.CompleteSignalReceived"/></item>
///   <item>根据信号时间戳(<see cref="IShiftManager.GetShiftAt"/>)判定信号归属哪个班次槽位</item>
///   <item>根据 <see cref="ITeamAssignmentService.GetTeamForSlot"/> 判定信号归属哪个班组</item>
///   <item>内存按 TeamId 分桶累计(<see cref="_countsByTeam"/>),毫秒级读</item>
///   <item>事件经 <see cref="Channel{T}"/> 异步批量写入数据库,带 TeamId 落库</item>
///   <item>班次周期切换时计数器清零,从 DB 复盘新周期已有数据</item>
///   <item>倒班(分配变更)时<b>不清</b>计数 — 各班组累计跟人走</item>
///   <item>启动期从 DB 恢复当前班次周期内每个班组的累计</item>
/// </list>
/// </summary>
public sealed class CountingService : ICountingService, IHostedService, IAsyncDisposable
{
    private const int ChannelCapacity = 10_000;
    private const int MaxBatchSize = 256;

    private readonly IPlcService _plc;
    private readonly IShiftManager _shifts;
    private readonly ITeamAssignmentService _teams;
    private readonly IProductionEventRepository _events;
    private readonly IProductionAnalyticsRepository _analytics;
    private readonly IGlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<CountingService> _logger;
    private readonly Channel<ProductionEvent> _channel;
    private readonly CancellationTokenSource _drainCts = new();
    private readonly Lock _gate = new();

    // 把"持续性故障"升级成 UI 红条告警,但要限流 —— 采集是热路径,
    // 故障(磁盘满/DB 锁)往往连续发生,不限流会刷爆 toast。每类故障最短间隔 30s 报一次。
    private static readonly TimeSpan AlertInterval = TimeSpan.FromSeconds(30);
    private readonly AlertThrottle _channelAlerts = new(AlertInterval);
    private readonly AlertThrottle _drainAlerts = new(AlertInterval);

    private Dictionary<int, int> _countsByTeam = new();
    private long _droppedSignals;
    private long _signalsWithoutTeam;
    private Task? _drainTask;
    private bool _stopped;
    private bool _disposed;

    /// <summary>注入 PLC / 班次 / 班组 / DB 仓库 / 全局异常告警 / 日志,创建有界 Channel 并订阅事件源。</summary>
    public CountingService(
        IPlcService plc,
        IShiftManager shifts,
        ITeamAssignmentService teams,
        IProductionEventRepository events,
        IProductionAnalyticsRepository analytics,
        IGlobalExceptionHandler exceptionHandler,
        ILogger<CountingService> logger)
    {
        _plc = plc;
        _shifts = shifts;
        _teams = teams;
        _events = events;
        _analytics = analytics;
        _exceptionHandler = exceptionHandler;
        _logger = logger;

        _channel = Channel.CreateBounded<ProductionEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        // 关键:订阅放构造里,确保任何 HostedService StartAsync 前已生效。
        _plc.CompleteSignalReceived += OnSignal;
        _shifts.ShiftChanged += OnShiftChanged;
        // 倒班分配变更:不重置计数,只把最新分配反映出来即可(订阅由 Dashboard 自己做)
    }

    /// <inheritdoc/>
    public int GetCountForTeam(int teamId)
    {
        lock (_gate) return _countsByTeam.TryGetValue(teamId, out var v) ? v : 0;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, int> CurrentCountsByTeam
    {
        get { lock (_gate) return new Dictionary<int, int>(_countsByTeam); }
    }

    /// <inheritdoc/>
    public event Action<int, int>? TeamCountChanged;

    // ── Hosted service lifecycle ─────────────────────────────────────

    /// <summary>启动期从 DB 复盘当前班次周期内每个班组的累计,然后启动后台 drain 任务。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动期复盘:从 DB 拉当前班次周期内每个班组的累计
        await ReloadCurrentPeriodAsync(cancellationToken);

        var current = _shifts.Current;
        if (current is null)
        {
            _logger.LogInformation("CountingService 启动 · 当前无活动班次");
        }
        else
        {
            int total;
            lock (_gate) total = _countsByTeam.Values.Sum();
            _logger.LogInformation(
                "CountingService 启动 · 当前班次 {Shift} 周期内累计 {Total} 件,分布在 {Teams} 个班组",
                current.Definition.Code, total, _countsByTeam.Count);
        }

        _drainTask = Task.Run(() => DrainAsync(_drainCts.Token), CancellationToken.None);
    }

    /// <summary>停止 — 取消订阅,Complete channel,等待 drain 任务结束(超时则强制取消)。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        _plc.CompleteSignalReceived -= OnSignal;
        _shifts.ShiftChanged -= OnShiftChanged;

        _channel.Writer.TryComplete();

        if (_drainTask is not null)
        {
            try
            {
                await _drainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { await _drainCts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { /* 已处理过,忽略 */ }
                try { await _drainTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }
        }

        if (Interlocked.Read(ref _droppedSignals) > 0)
        {
            _logger.LogWarning(
                "CountingService 退出 · 累计丢弃信号 {Dropped} 条(写入通道积压)",
                _droppedSignals);
        }
        if (Interlocked.Read(ref _signalsWithoutTeam) > 0)
        {
            _logger.LogWarning(
                "CountingService 退出 · 累计无班组归属丢弃 {Count} 条信号 — 检查 ShiftSlotAssignment 是否齐全",
                _signalsWithoutTeam);
        }
    }

    /// <summary>异步释放 — 调用 StopAsync 并 dispose 内部 CTS。幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _drainCts.Dispose();
    }

    // ── Event handlers ───────────────────────────────────────────────

    private void OnSignal(DateTime utcNow)
    {
        // 按信号自己的时间戳判断归属哪个班次,而不是用 _shifts.Current:
        // Current 由定时检测更新,班次交接后的一小段时间里可能还是上一个班次,
        // 用它会把新班次的产量记到上一个班次 / 班组头上。
        var shift = _shifts.GetShiftAt(utcNow.ToLocalTime());
        if (shift is null)
        {
            _logger.LogDebug("信号到达但不在任何班次时段内,丢弃 (utcNow={UtcNow:O})", utcNow);
            return;
        }

        // 关键:查该槽位的班组分配。无分配则不能记录(否则数据归属错乱)。
        var team = _teams.GetTeamForSlot(shift.Definition.Id);
        if (team is null)
        {
            Interlocked.Increment(ref _signalsWithoutTeam);
            _logger.LogWarning(
                "信号到达但班次 {Shift} (Id={ShiftDefId}) 无班组分配,丢弃。请到「班次配置 → 班组管理」分配班组。",
                shift.Definition.Code, shift.Definition.Id);
            return;
        }

        // 内存计数只累计当前周期(界面 Hero 大数字显示的那个)。信号已属于下一个周期、
        // 但 ShiftManager 还没切换时,只落库不计内存:随后的班次切换会从数据库重新加载新周期的累计。
        var isCurrentPeriod = IsSamePeriod(shift, _shifts.Current);
        var newCount = 0;
        if (isCurrentPeriod)
        {
            lock (_gate)
            {
                _countsByTeam.TryGetValue(team.Id, out var cur);
                newCount = cur + 1;
                _countsByTeam[team.Id] = newCount;
            }
        }

        var evt = new ProductionEvent(
            Id: 0,
            Timestamp: utcNow,
            ShiftDate: shift.ShiftDate,
            ShiftDefId: shift.Definition.Id,
            TeamId: team.Id,
            Source: "PLC");

        if (!_channel.Writer.TryWrite(evt))
        {
            var dropped = Interlocked.Increment(ref _droppedSignals);
            // 内存计数已 +1,但落库被丢弃 → 内存与 DB 出现差值(重启 reload 会修正,但运行期不一致)。
            // 实时 Warning + 限流 UI 告警,让操作员立刻知道"屏幕数字对、但归档可能少"。
            _logger.LogWarning(
                "写入通道积压,本次事件落库被丢弃 · TeamId={TeamId} · 累计丢弃 {Dropped} 条", team.Id, dropped);
            if (_channelAlerts.ShouldReport())
            {
                _exceptionHandler.Report(
                    new InvalidOperationException(
                        $"计数落库通道积压,已累计丢弃 {dropped} 条事件(内存计数正确,但数据库落库滞后)"),
                    "CountingService.Channel");
            }
        }

        if (isCurrentPeriod) RaiseTeamCountChanged(team.Id, newCount);
    }

    private static bool IsSamePeriod(ShiftAssignment shift, ShiftAssignment? current) =>
        current is not null
        && shift.Definition.Id == current.Definition.Id
        && shift.ShiftDate == current.ShiftDate;

    private void OnShiftChanged(ShiftAssignment? oldShift, ShiftAssignment? newShift)
    {
        // 班次周期切换两阶段处理(避免切换瞬间少计):
        //
        // 1. 立刻清空内存计数 + 给 UI 发 0 复位,让 Hero 数字立刻翻到新周期的 0
        // 2. 延迟 2 秒后从 DB 重新 reload — 这一刻 channel 已经把
        //    切换瞬间还在 in-flight 的事件全部写入 DB,reload 把它们捞回内存。
        //
        // 原 bug:OnSignal 读 _shifts.Current = NewShift 增了 dict[newTeam]+1,
        // 但 ShiftChanged 处理紧随其后 Clear() 把这个增量清掉了;event 仍正常
        // 写到 DB,导致 DB 比内存多 1 条 — 内存累计永久偏低,直到下次启动 reload。
        //
        // 修复后:Clear() 立即但加 2s 延迟 reload,把那些 in-flight event 捞回。
        _logger.LogInformation(
            "CountingService 班次周期切换 · {Old} → {New} · 清空内存计数,2s 后从 DB 复盘",
            oldShift?.Definition.Code ?? "(空档)",
            newShift?.Definition.Code ?? "(空档)");

        IReadOnlyList<int> previousTeamIds;
        lock (_gate)
        {
            previousTeamIds = _countsByTeam.Keys.ToArray();
            _countsByTeam.Clear();
        }
        // 给所有原本有计数的班组各发一个 0(让 UI 复位)
        foreach (var tid in previousTeamIds) RaiseTeamCountChanged(tid, 0);

        // 延迟 reload。2s 给 channel drain + DB 写入留充足缓冲(channel batch
        // 最长 256 条,落库 < 100ms;切换瞬间最多有几十个 in-flight 事件)。
        // 用 _drainCts.Token 让 Dispose 时能取消这个延迟任务。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _drainCts.Token).ConfigureAwait(false);
                await ReloadCurrentPeriodAsync(_drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出 — 服务被关闭了
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "班次切换后延迟 reload 失败 — 内存计数可能短暂落后 DB,下次启动会修正");
                // 班次切换低频(每班一次),reload 失败会让 Hero 大数字停在 0,操作员必须知道。
                _exceptionHandler.Report(ex, "CountingService.Reload");
            }
        });
    }

    // ── 从 DB 复盘当前班次周期内每个班组累计 ─────────────────────────

    private async Task ReloadCurrentPeriodAsync(CancellationToken cancellationToken)
    {
        var current = _shifts.Current;
        if (current is null)
        {
            lock (_gate) _countsByTeam.Clear();
            return;
        }

        // [PeriodStart, PeriodEnd) 是本地时间,DB 存 UTC,需要转换
        var fromUtc = current.PeriodStart.ToUniversalTime();
        var toUtc = current.PeriodEnd.ToUniversalTime();
        var dict = await _analytics.CountEventsByTeamInRangeAsync(fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, int> snapshot;
        lock (_gate)
        {
            _countsByTeam = new Dictionary<int, int>(dict);
            snapshot = new Dictionary<int, int>(_countsByTeam);
        }
        foreach (var (teamId, count) in snapshot)
        {
            RaiseTeamCountChanged(teamId, count);
        }
    }

    // ── Background drain loop ────────────────────────────────────────

    private async Task DrainAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        var batch = new List<ProductionEvent>(MaxBatchSize);

        try
        {
            await foreach (var first in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                batch.Add(first);

                while (batch.Count < MaxBatchSize && reader.TryRead(out var more))
                {
                    batch.Add(more);
                }

                try
                {
                    await _events.InsertEventsAsync(batch, ct).ConfigureAwait(false);
                    _logger.LogDebug("批量落库 {Count} 条事件", batch.Count);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "批量落库失败,丢弃本批 {Count} 条事件 (内存计数器仍准确)", batch.Count);
                    // 落库失败 = DB 在丢数据(磁盘满/锁/损坏),屏幕数字仍正常 → 必须告警 UI。
                    if (_drainAlerts.ShouldReport()) _exceptionHandler.Report(ex, "CountingService.Drain");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    private void RaiseTeamCountChanged(int teamId, int count)
    {
        try { TeamCountChanged?.Invoke(teamId, count); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TeamCountChanged 订阅方抛出异常");
        }
    }
}
