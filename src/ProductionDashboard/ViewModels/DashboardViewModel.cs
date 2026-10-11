using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Plc;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Core.Shifts;
using ProductionDashboard.Core.Statistics;
using ProductionDashboard.ViewModels.Dashboard;

namespace ProductionDashboard.ViewModels;

/// <summary>
/// Dashboard 主视图的 view-model — facade。把展示责任分摊给两个 presenter:
/// <list type="bullet">
///   <item><see cref="Hero"/>(<see cref="HeroAndShiftPresenter"/>):Hero 大数字 + 白班 / 夜班对比卡(事件驱动 + 周期刷新)</item>
///   <item><see cref="Metrics"/>(<see cref="AggregateMetricsPresenter"/>):5 维度卡 + 累计对比表 + 5 张 mini 柱图(2 秒周期刷新)</item>
/// </list>
/// facade 自己只持有跨切面状态(PLC 连接 / 顶栏倒计时的当前班次)和 2 秒 timer 编排。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IPlcService _plc;
    private readonly IShiftManager _shifts;
    private readonly ITeamAssignmentService _teamAssign;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _refreshTimer;
    private int _refreshing;   // 1 = 上一次聚合刷新还没结束
    private bool _disposed;

    /// <summary>注入 PLC / 班次 / 计数 / 班组 / 分析仓库 / 日志,构造两个 presenter 并启动 2 秒 timer。</summary>
    public DashboardViewModel(
        IPlcService plc,
        IShiftManager shifts,
        ICountingService counter,
        ITeamAssignmentService teamAssign,
        IProductionStatisticsService statistics,
        ILogger<DashboardViewModel> logger)
    {
        _plc = plc;
        _shifts = shifts;
        _teamAssign = teamAssign;
        _logger = logger;
        _ui = Application.Current.Dispatcher;

        Hero = new HeroAndShiftPresenter(shifts, counter, teamAssign, statistics, _ui);
        Metrics = new AggregateMetricsPresenter(statistics, _ui);

        // PLC 状态 — 跨切面,留在 facade。订阅前先同步当前值,
        // 因为 PlcService 可能在 ConnectionStateChanged 订阅之前已经连上了。
        _plc.ConnectionStateChanged += OnPlcConnectionChanged;
        PlcConnected = _plc.IsConnected;

        // 班次变化时让聚合刷新(下次 timer 跳之前先跑一次)。Hero 卡的标签由 presenter 自己订阅。
        _shifts.ShiftChanged += OnShiftChanged;
        _shifts.DefinitionsChanged += OnDefinitionsChanged;
        _teamAssign.AssignmentsChanged += OnAssignmentsChanged;

        // 周期性查询 (2s) 刷新两个 presenter
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => SafeFireAndForget(RefreshAggregatesAsync(), nameof(RefreshAggregatesAsync)),
            _ui);
        _refreshTimer.Start();

        // 首次立刻刷一次,不等 2s
        SafeFireAndForget(RefreshAggregatesAsync(), nameof(RefreshAggregatesAsync));
    }

    /// <summary>Hero 大数字 + 白班 / 夜班对比 presenter(事件驱动)。</summary>
    public HeroAndShiftPresenter Hero { get; }
    /// <summary>5 维度卡 + 累计对比 + mini 柱图 presenter(2 秒周期刷新)。</summary>
    public AggregateMetricsPresenter Metrics { get; }

    // ── 跨切面 ObservableProperties ────────────────────────────────

    /// <summary>PLC 状态(供顶栏 / 错误条订阅)。默认 false:启动那一刻假设未连接。</summary>
    [ObservableProperty] public partial bool PlcConnected { get; set; }

    /// <summary>当前班次对象,供 MainViewModel 顶栏倒计时用。null 表示空档期。</summary>
    public ShiftAssignment? CurrentAssignment => _shifts.Current;

    // ── 编排 ───────────────────────────────────────────────────────

    private async Task RefreshAggregatesAsync()
    {
        // 上一次还没结束就跳过:大库查询偶尔超过 2 秒时,刷新请求不会越积越多
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var ct = CancellationToken.None;
            // 查询放到线程池执行。SQLite 的异步方法实际上是同步完成的,
            // 直接在 UI 线程上 await 会让整个查询期间界面卡住。
            // 两个 presenter 各自查库(SQLite 单写多读,并发读安全),结果再通过 dispatcher 回到 UI 线程。
            await Task.WhenAll(
                Task.Run(() => Metrics.RefreshAsync(ct), ct),
                Task.Run(() => Hero.RefreshFromAnalyticsAsync(ct), ct)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard 聚合查询失败");
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void OnPlcConnectionChanged(bool isConnected)
    {
        _ui.InvokeAsync(() => PlcConnected = isConnected);
    }

    private void OnShiftChanged(ShiftAssignment? oldShift, ShiftAssignment? newShift)
    {
        // 班次切换后立刻刷一次聚合,不等下一次 2s timer(否则 5 维度卡会滞后)
        SafeFireAndForget(RefreshAggregatesAsync(), nameof(RefreshAggregatesAsync));
    }

    private void OnDefinitionsChanged()
    {
        // 班次定义改变(target / name / color)→ 5 维度卡的 TargetText 跟着变
        SafeFireAndForget(RefreshAggregatesAsync(), nameof(RefreshAggregatesAsync));
    }

    private void OnAssignmentsChanged()
    {
        // 倒班 → 班组维度的累计对比可能换标签了
        SafeFireAndForget(RefreshAggregatesAsync(), nameof(RefreshAggregatesAsync));
    }

    /// <summary>
    /// "丢弃 Task 但兜底观察异常"的 helper。直接 _ = SomeAsync() 在 .NET 里
    /// 是个潜在坑:Task 抛了且没人 await 时,GC 会触发 TaskScheduler.UnobservedTaskException,
    /// 继而被全局 handler 当成"系统异常"弹红条。ContinueWith 显式观察,避免 silent leak。
    /// </summary>
    private void SafeFireAndForget(Task task, string label)
    {
        _ = task.ContinueWith(
            t =>
            {
                if (t.Exception is { } ex)
                {
                    _logger.LogError(ex, "未观察到的 Task 异常 [{Label}]", label);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>取消订阅所有事件并停 timer,幂等。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _plc.ConnectionStateChanged -= OnPlcConnectionChanged;
        _shifts.ShiftChanged -= OnShiftChanged;
        _shifts.DefinitionsChanged -= OnDefinitionsChanged;
        _teamAssign.AssignmentsChanged -= OnAssignmentsChanged;
        Hero.Dispose();
        _refreshTimer.Stop();
    }
}
