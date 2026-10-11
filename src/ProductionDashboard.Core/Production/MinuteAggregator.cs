using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;

namespace ProductionDashboard.Core.Production;

/// <summary>
/// 把原始事件聚合成分钟级的 <c>ProductionMinute</c> 行(本周 / 本月 / 本年累计都从这里求和)。由调度器每分钟触发。
/// 从 AggregationHostedService 拆出,独立可测。
/// </summary>
public interface IMinuteAggregator
{
    /// <summary>重算最近几分钟以及上次之后漏掉的分钟。内部已处理异常 + 限流告警。</summary>
    Task AggregateAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMinuteAggregator"/>
/// <remarks>
/// 不能只聚合"上一分钟":定时器偶尔晚到会跳过某一分钟、某次聚合失败、程序重启、事件在聚合之后才落库,
/// 都会让分钟表出现空洞或少计,而累计数字是从分钟表求和的。因此每次都重算:
/// <list type="bullet">
///   <item>最近 <see cref="Lookback"/> 分钟(补上晚落库的事件)</item>
///   <item>以及上次成功聚合之后的所有分钟;程序刚启动时从数据库里最后聚合到的那一分钟补起</item>
/// </list>
/// 分钟聚合只取决于原始事件,重复计算同一分钟是安全的。
/// </remarks>
public sealed class MinuteAggregator : IMinuteAggregator
{
    /// <summary>每次至少重算最近这么多分钟。</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromMinutes(3);

    private readonly IProductionEventRepository _events;
    private readonly IGlobalExceptionHandler _exceptionHandler;
    private readonly ILogger<MinuteAggregator> _logger;
    private readonly TimeProvider _clock;
    private readonly AlertThrottle _alerts;

    private DateTime? _aggregatedUpTo;   // 已成功聚合到的位置(UTC 整分钟,不含)
    private int _running;                // 1 = 上一次还在执行

    /// <summary>注入事件仓库 / 全局异常告警 / 日志。<paramref name="clock"/> 可注入测试时钟。</summary>
    public MinuteAggregator(
        IProductionEventRepository events,
        IGlobalExceptionHandler exceptionHandler,
        ILogger<MinuteAggregator> logger,
        TimeProvider? clock = null)
    {
        _events = events;
        _exceptionHandler = exceptionHandler;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _alerts = new AlertThrottle(TimeSpan.FromSeconds(30), _clock);
    }

    /// <inheritdoc/>
    public async Task AggregateAsync(CancellationToken cancellationToken)
    {
        // 上一次还没跑完(数据库慢)就跳过这次,下一次会覆盖这段时间
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var nowUtc = _clock.GetUtcNow().UtcDateTime;
            var thisMinute = new DateTime(
                nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
            var from = await GetStartAsync(thisMinute, cancellationToken).ConfigureAwait(false);

            var minutes = await _events.RebuildMinuteAggregatesAsync(from, thisMinute, cancellationToken)
                .ConfigureAwait(false);
            _aggregatedUpTo = thisMinute;

            if (thisMinute - from > Lookback)
            {
                _logger.LogInformation(
                    "分钟聚合补算 {From:O} – {To:O} · 写入 {Minutes} 分钟", from, thisMinute, minutes);
            }
            else
            {
                _logger.LogDebug("分钟聚合 {From:O} – {To:O} · 写入 {Minutes} 分钟", from, thisMinute, minutes);
            }
        }
        catch (Exception ex)
        {
            // 失败时 _aggregatedUpTo 不前进,下一次会把这段时间一起补上
            _logger.LogError(ex, "分钟聚合失败,下一次会重试这段时间");
            if (_alerts.ShouldReport()) _exceptionHandler.Report(ex, "Aggregation.Minute");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<DateTime> GetStartAsync(DateTime thisMinute, CancellationToken cancellationToken)
    {
        var recent = thisMinute - Lookback;
        var resumeFrom = _aggregatedUpTo
            ?? await _events.GetLatestAggregatedMinuteAsync(cancellationToken).ConfigureAwait(false);
        return resumeFrom is { } r && r < recent ? r : recent;
    }
}
