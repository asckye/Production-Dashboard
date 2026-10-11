using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班次状态服务的具体实现。组合:
/// <list type="bullet">
///   <item><see cref="IShiftDefinitionRepository"/> — 数据库加载</item>
///   <item><see cref="ShiftCalculator"/> — 纯逻辑判定</item>
///   <item><see cref="TimeProvider"/> — 可注入的时钟(便于测试)</item>
///   <item><c>ITimer</c> — 周期性检测班次切换</item>
/// </list>
/// </summary>
public sealed class ShiftManager : IShiftManager, IHostedService, IAsyncDisposable
{
    private readonly IShiftDefinitionRepository _repo;
    private readonly ShiftOptions _options;
    private readonly ILogger<ShiftManager> _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private IReadOnlyList<ShiftDefinition> _definitions = Array.Empty<ShiftDefinition>();
    private ShiftAssignment? _current;
    private ITimer? _timer;
    private bool _stopped;

    /// <summary>注入仓库 / 选项 / 日志,<paramref name="clock"/> 可注入测试时钟。</summary>
    public ShiftManager(
        IShiftDefinitionRepository repo,
        IOptions<ShiftOptions> options,
        ILogger<ShiftManager> logger,
        TimeProvider? clock = null)
    {
        _repo = repo;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public ShiftAssignment? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ShiftDefinition> Definitions
    {
        get { lock (_gate) return _definitions; }
    }

    /// <inheritdoc/>
    public event Action<ShiftAssignment?, ShiftAssignment?>? ShiftChanged;
    /// <inheritdoc/>
    public event Action? DefinitionsChanged;

    /// <inheritdoc/>
    public ShiftAssignment? GetShiftAt(DateTime localTime)
    {
        IReadOnlyList<ShiftDefinition> snapshot;
        lock (_gate) snapshot = _definitions;
        return ShiftCalculator.Compute(snapshot, localTime, _options.AttributionRule);
    }

    /// <inheritdoc/>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var nowLocal = _clock.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(nowLocal);

        var fresh = await _repo.GetActiveShiftDefinitionsAsync(today, cancellationToken);

        lock (_gate) _definitions = fresh;
        _logger.LogInformation(
            "加载班次定义 · {Count} 个 · 日期 {Today}", fresh.Count, today);

        // 1) 重算 _current — 哪怕班次身份没变,也要让 _current.Definition 指向最新引用,
        //    这样 Dashboard 读 Current.Definition.TargetCount 就是新值
        EvaluateAndRaise(forceUpdateCurrent: true);

        // 2) 通知订阅方"定义集合刷新过了" — Dashboard 借此立刻刷新 A/B 标签 / 目标
        try { DefinitionsChanged?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "DefinitionsChanged 订阅方抛出异常"); }
    }

    /// <summary>启动 — 先 RefreshAsync 一次,然后按 ShiftOptions.CheckIntervalMs 周期检测班次切换。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);

        var period = TimeSpan.FromMilliseconds(_options.CheckIntervalMs);
        _timer = _clock.CreateTimer(_ => EvaluateAndRaise(), state: null, period, period);
        _logger.LogInformation("ShiftManager 启动 · 每 {Period} 检测一次班次切换", period);
    }

    /// <summary>停止 — dispose timer,幂等。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }
        _logger.LogInformation("ShiftManager 停止");
    }

    /// <summary>异步释放 — 委派给 StopAsync,幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void EvaluateAndRaise(bool forceUpdateCurrent = false)
    {
        ShiftAssignment? oldAssignment;
        ShiftAssignment? newAssignment;

        var nowLocal = _clock.GetLocalNow().DateTime;
        IReadOnlyList<ShiftDefinition> defs;
        lock (_gate) defs = _definitions;

        newAssignment = ShiftCalculator.Compute(defs, nowLocal, _options.AttributionRule);

        bool changed;
        lock (_gate)
        {
            oldAssignment = _current;
            changed = !IsSameAssignment(oldAssignment, newAssignment);
            // forceUpdateCurrent=true 时(由 RefreshAsync 调用)总是用新引用替换 _current,
            // 这样即便班次身份没变,Definition 字段(target / name / color)的更新也能被订阅方读到
            if (changed || forceUpdateCurrent) _current = newAssignment;
        }

        if (!changed) return;

        _logger.LogInformation(
            "班次切换 · {Old} → {New}",
            Describe(oldAssignment), Describe(newAssignment));

        // 用 try/catch 防止某个订阅方异常影响后续订阅方 + 后续 tick
        try
        {
            ShiftChanged?.Invoke(oldAssignment, newAssignment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ShiftChanged 订阅方抛出异常");
        }
    }

    private static bool IsSameAssignment(ShiftAssignment? a, ShiftAssignment? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Definition.Id == b.Definition.Id && a.ShiftDate == b.ShiftDate;
    }

    private static string Describe(ShiftAssignment? a) =>
        a is null ? "(空档)" : $"{a.Definition.Code} {a.Definition.Name} @ {a.ShiftDate:yyyy-MM-dd}";
}
