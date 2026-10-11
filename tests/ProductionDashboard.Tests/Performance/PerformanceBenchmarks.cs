using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;
using Xunit.Abstractions;

namespace ProductionDashboard.Tests.Performance;

/// <summary>
/// 端到端性能基准:覆盖生产看板的关键热路径。
/// <list type="bullet">
///   <item>Channel + 批量 INSERT:模拟 PLC 信号峰值吞吐</item>
///   <item>5 维度卡 COUNT(*) 在 1k / 10k / 100k 行下的延迟</item>
///   <item>Hourly / Daily 聚合查询(mini 柱图数据源)</item>
///   <item>ShiftDate 列加索引前后的对比</item>
/// </list>
/// 默认不在 CI 跑,只在排查性能时手动触发:
/// <c>dotnet test --filter "Category=Performance" -v normal</c>
/// </summary>
[Trait("Category", "Performance")]
public sealed class PerformanceBenchmarks : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdperf_{Guid.NewGuid():N}.db");
    private readonly ITestOutputHelper _output;

    private SqliteConnectionFactory _factory = null!;
    private ProductionEventRepository _events = null!;
    private ProductionAnalyticsRepository _analytics = null!;

    public PerformanceBenchmarks(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new DatabaseOptions
        {
            Path = _dbPath,
            BackupPath = Path.GetDirectoryName(_dbPath)!,
            BackupRetentionDays = 7,
            VacuumTime = "02:00",
            BackupTime = "03:00",
        });

        _factory = new SqliteConnectionFactory(options);
        var initializer = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.StartAsync(CancellationToken.None);

        _events = new ProductionEventRepository(_factory);
        _analytics = new ProductionAnalyticsRepository(_factory);
    }

    public Task DisposeAsync()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-shm", "-wal" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { /* 清理失败不影响测试结果 */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 批量插入吞吐:测在 1 / 10 / 100 / 1000 条/批的不同 batch size 下的写入速度。
    /// 验证 CountingService 的批量 channel drain(MaxBatchSize=256)是否合理。
    /// </summary>
    [Fact]
    public async Task Insert_throughput_by_batch_size()
    {
        const int totalEvents = 10_000;
        var batchSizes = new[] { 1, 16, 64, 256, 1024 };
        var today = DateOnly.FromDateTime(DateTime.Today);

        _output.WriteLine("=== 批量 INSERT 吞吐(10000 条事件) ===");
        _output.WriteLine($"{"BatchSize",10} {"Elapsed(ms)",12} {"Throughput(件/s)",18}");

        foreach (var batchSize in batchSizes)
        {
            // 每次测试前清空表,避免相互干扰
            await using (var conn = _factory.OpenWrite())
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM ProductionEvent;";
                await cmd.ExecuteNonQueryAsync();
            }

            var sw = Stopwatch.StartNew();
            var written = 0;
            while (written < totalEvents)
            {
                var batch = new List<ProductionEvent>(batchSize);
                for (var i = 0; i < batchSize && written + i < totalEvents; i++)
                {
                    batch.Add(new ProductionEvent(
                        Id: 0,
                        Timestamp: DateTime.UtcNow,
                        ShiftDate: today,
                        ShiftDefId: 1,
                        TeamId: 1,
                        Source: "BENCH"));
                }
                await _events.InsertEventsAsync(batch, CancellationToken.None);
                written += batch.Count;
            }
            sw.Stop();

            var throughput = totalEvents / sw.Elapsed.TotalSeconds;
            _output.WriteLine($"{batchSize,10} {sw.ElapsedMilliseconds,12} {throughput,18:N0}");
        }
    }

    /// <summary>
    /// 5 维度卡的主力查询(ShiftDate 范围 COUNT)在不同数据量下的延迟。
    /// 这是 Dashboard 每 2 秒跑 1 次的查询。
    /// </summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task CountEventsByShiftDateRange_latency(int rowCount)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = today.AddDays(-6);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var yearStart = new DateOnly(today.Year, 1, 1);

        await SeedEvents(rowCount, today.AddDays(-365), today);

        // 预热 — SQLite 第一次查询要做缓存,排除冷启动噪音
        await _analytics.CountEventsByShiftDateRangeAsync(today, today, null, CancellationToken.None);

        // 5 个维度各跑 10 次取中位数
        var dimensions = new (string label, DateOnly from, DateOnly to)[]
        {
            ("hour(today)", today, today),
            ("day", today, today),
            ("week", weekStart, today),
            ("month", monthStart, today),
            ("year", yearStart, today),
        };

        _output.WriteLine($"=== CountEventsByShiftDateRange · {rowCount:N0} 行 ===");
        _output.WriteLine($"{"Dimension",-12} {"Median(μs)",12} {"P95(μs)",12}");

        foreach (var (label, from, to) in dimensions)
        {
            var samples = new long[50];
            for (var i = 0; i < samples.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = await _analytics.CountEventsByShiftDateRangeAsync(
                    from, to, null, CancellationToken.None);
                sw.Stop();
                samples[i] = sw.Elapsed.Ticks;
            }
            Array.Sort(samples);
            var median = samples[samples.Length / 2] / 10.0;     // ticks → μs(100ns/tick)
            var p95 = samples[(int)(samples.Length * 0.95)] / 10.0;
            _output.WriteLine($"{label,-12} {median,12:F1} {p95,12:F1}");
        }
    }

    /// <summary>
    /// Hero 卡 / mini 柱图的复合查询(并行 5 个维度 + 班组维度 + 小时维度)。
    /// 模拟 AggregateMetricsPresenter.RefreshAsync 一次完整刷新的总耗时。
    /// </summary>
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task FullDashboardRefresh_latency(int rowCount)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await SeedEvents(rowCount, today.AddDays(-365), today);

        // 预热
        await SimulateDashboardRefresh(today);

        var samples = new long[20];
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            await SimulateDashboardRefresh(today);
            sw.Stop();
            samples[i] = sw.ElapsedMilliseconds;
        }
        Array.Sort(samples);

        _output.WriteLine($"=== 一次完整 Dashboard 刷新 · {rowCount:N0} 行 ===");
        _output.WriteLine($"中位数: {samples[10]} ms");
        _output.WriteLine($"P95:    {samples[19]} ms");
        _output.WriteLine($"最差:   {samples[^1]} ms");
        _output.WriteLine("说明:UI 是每 2 秒刷新一次,只要 P95 < 200ms 就完全够用");
    }

    /// <summary>
    /// 验证给 ShiftDate 列加索引能否显著加速 5 维度查询。
    /// 对比 100k 行下:无索引 vs 有索引的 year 维度 COUNT 延迟。
    /// </summary>
    [Fact]
    public async Task ShiftDate_index_benefit()
    {
        const int rowCount = 100_000;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var yearStart = new DateOnly(today.Year, 1, 1);

        await SeedEvents(rowCount, today.AddDays(-365), today);

        // 1) 无索引基线 — v1 schema 默认情况
        await _analytics.CountEventsByShiftDateRangeAsync(today, today, null, CancellationToken.None);
        var noIndex = MedianMicros(50, () =>
            _analytics.CountEventsByShiftDateRangeAsync(yearStart, today, null, CancellationToken.None));

        // 2) 加上 ShiftDate 索引
        await using (var conn = _factory.OpenWrite())
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_ProductionEvent_ShiftDate ON ProductionEvent(ShiftDate);";
            await cmd.ExecuteNonQueryAsync();
        }
        await _analytics.CountEventsByShiftDateRangeAsync(today, today, null, CancellationToken.None);
        var withIndex = MedianMicros(50, () =>
            _analytics.CountEventsByShiftDateRangeAsync(yearStart, today, null, CancellationToken.None));

        _output.WriteLine($"=== ShiftDate 索引收益 · {rowCount:N0} 行 · year 维度 COUNT ===");
        _output.WriteLine($"无索引: {noIndex,8:F1} μs");
        _output.WriteLine($"有索引: {withIndex,8:F1} μs");
        var speedup = noIndex / Math.Max(1.0, withIndex);
        _output.WriteLine($"提速:   {speedup:F1}x");
    }

    /// <summary>
    /// 启动期复盘的查询(CountEventsByTeamInRangeAsync)在 1 年事件量下的延迟。
    /// 这是 CountingService 启动期跑的,只跑一次但要快(< 50ms 才不感知)。
    /// </summary>
    [Fact]
    public async Task Startup_replay_latency()
    {
        const int rowCount = 100_000;
        var today = DateOnly.FromDateTime(DateTime.Today);
        await SeedEvents(rowCount, today.AddDays(-365), today);

        // 启动期复盘:取最近一个班次周期(8h)内的事件,按 TeamId 分桶 COUNT
        var nowUtc = DateTime.UtcNow;
        var fromUtc = nowUtc.AddHours(-8);

        await _analytics.CountEventsByTeamInRangeAsync(fromUtc, nowUtc, CancellationToken.None);

        var samples = new long[20];
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = await _analytics.CountEventsByTeamInRangeAsync(fromUtc, nowUtc, CancellationToken.None);
            sw.Stop();
            samples[i] = sw.ElapsedMilliseconds;
        }
        Array.Sort(samples);

        _output.WriteLine($"=== 启动期复盘(8h 班次周期 / {rowCount:N0} 行) ===");
        _output.WriteLine($"中位数: {samples[10]} ms");
        _output.WriteLine($"最差:   {samples[^1]} ms");
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task SeedEvents(int count, DateOnly from, DateOnly to)
    {
        // 把事件均匀洒在 [from, to] 区间内,模拟真实生产分布
        var totalDays = (to.DayNumber - from.DayNumber) + 1;
        var perDay = Math.Max(1, count / totalDays);
        var written = 0;
        const int batchSize = 1000;

        for (var d = from; d <= to && written < count; d = d.AddDays(1))
        {
            for (var i = 0; i < perDay && written < count; i += batchSize)
            {
                var size = Math.Min(batchSize, Math.Min(perDay - i, count - written));
                var batch = new List<ProductionEvent>(size);
                for (var k = 0; k < size; k++)
                {
                    // Timestamp 在当天均匀分散
                    var hour = (k % 24);
                    var minute = (k * 7) % 60;
                    var ts = d.ToDateTime(new TimeOnly(hour, minute)).ToUniversalTime();
                    batch.Add(new ProductionEvent(
                        Id: 0,
                        Timestamp: ts,
                        ShiftDate: d,
                        ShiftDefId: (k % 2) + 1,    // 1=A, 2=B
                        TeamId: (k % 2) + 1,
                        Source: "BENCH"));
                }
                await _events.InsertEventsAsync(batch, CancellationToken.None);
                written += batch.Count;
            }
        }
    }

    private async Task SimulateDashboardRefresh(DateOnly today)
    {
        var weekStart = today.AddDays(-6);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var yearStart = new DateOnly(today.Year, 1, 1);
        var nowUtc = DateTime.UtcNow;
        var hourStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);

        // 模拟 AggregateMetricsPresenter.RefreshAsync — 并行 5 维度查询
        await Task.WhenAll(
            _analytics.CountEventsInRangeAsync(hourStartUtc, hourStartUtc.AddHours(1), CancellationToken.None),
            _analytics.CountEventsByShiftDateRangeAsync(today, today, null, CancellationToken.None),
            _analytics.CountEventsByShiftDateRangeAsync(weekStart, today, null, CancellationToken.None),
            _analytics.CountEventsByShiftDateRangeAsync(monthStart, today, null, CancellationToken.None),
            _analytics.CountEventsByShiftDateRangeAsync(yearStart, today, null, CancellationToken.None),
            _analytics.GetHourlyCountsForDateAsync(today, CancellationToken.None),
            _analytics.GetDailyCountsAsync(today.AddDays(-6), today, CancellationToken.None));
    }

    private static double MedianMicros(int samples, Func<Task<int>> action)
    {
        var ticks = new long[samples];
        for (var i = 0; i < samples; i++)
        {
            var sw = Stopwatch.StartNew();
            action().GetAwaiter().GetResult();
            sw.Stop();
            ticks[i] = sw.Elapsed.Ticks;
        }
        Array.Sort(ticks);
        return ticks[samples / 2] / 10.0;
    }
}
