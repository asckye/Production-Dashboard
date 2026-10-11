using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;
using ProductionDashboard.Infrastructure.Persistence.Repositories;
using ProductionDashboard.Tests.Helpers;

namespace ProductionDashboard.Tests.Core.Production;

/// <summary>
/// MinuteAggregator 的集成测试(真实 SQLite)。重点是"不留空洞":
/// 定时器晚到、某次失败、程序重启、事件晚落库之后,分钟表都要和原始事件一致。
/// </summary>
public sealed class MinuteAggregatorTests : IAsyncLifetime
{
    private const int TeamA = 1;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdma_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private ProductionEventRepository _events = null!;
    private ControlledTimeProvider _clock = null!;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new DatabaseOptions
        {
            Path = _dbPath,
            BackupPath = Path.GetDirectoryName(_dbPath)!,
            VacuumTime = "02:00",
            BackupTime = "03:00",
        });
        _factory = new SqliteConnectionFactory(options);
        var init = new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance);
        await init.StartAsync(CancellationToken.None);

        _events = new ProductionEventRepository(_factory);
        _clock = new ControlledTimeProvider(); // now = 2026-01-01 00:00:00 UTC
    }

    public Task DisposeAsync()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-shm", "-wal" })
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    private MinuteAggregator MakeSut() => new(
        _events,
        new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance),
        NullLogger<MinuteAggregator>.Instance,
        _clock);

    private async Task SeedEventsAsync(DateTime minuteUtc, int n)
    {
        var shiftDate = DateOnly.FromDateTime(minuteUtc);
        var list = Enumerable.Range(0, n)
            .Select(i => new ProductionEvent(0, minuteUtc.AddSeconds(10 + i), shiftDate, 1, TeamA))
            .ToList();
        await _events.InsertEventsAsync(list, CancellationToken.None);
    }

    private async Task<(long Count, long TeamId)?> ReadMinuteAsync(DateTime minuteUtc)
    {
        await using var conn = _factory.OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Count, TeamId FROM ProductionMinute WHERE MinuteStart = @m";
        cmd.Parameters.AddWithValue("@m", minuteUtc.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    [Fact]
    public async Task Writes_previous_minute_with_its_team()
    {
        var prevMinute = NowUtc.AddMinutes(-1);
        await SeedEventsAsync(prevMinute, 3);

        await MakeSut().AggregateAsync(CancellationToken.None);

        (await ReadMinuteAsync(prevMinute)).Should().Be((3L, (long)TeamA));
    }

    [Fact]
    public async Task Writes_nothing_when_there_are_no_events()
    {
        await MakeSut().AggregateAsync(CancellationToken.None);

        (await ReadMinuteAsync(NowUtc.AddMinutes(-1))).Should().BeNull("无事件时不应写分钟桶");
    }

    [Fact]
    public async Task Does_not_aggregate_the_minute_that_is_still_in_progress()
    {
        await SeedEventsAsync(NowUtc, 2);

        await MakeSut().AggregateAsync(CancellationToken.None);

        (await ReadMinuteAsync(NowUtc)).Should().BeNull("当前分钟还没结束");
    }

    [Fact]
    public async Task Minutes_missed_while_the_timer_did_not_run_are_caught_up()
    {
        var sut = MakeSut();
        await sut.AggregateAsync(CancellationToken.None);
        var start = NowUtc;

        // 定时器 10 分钟没有执行(系统忙 / 某几次失败),期间有生产
        _clock.Advance(TimeSpan.FromMinutes(10));
        await SeedEventsAsync(start.AddMinutes(2), 4);

        await sut.AggregateAsync(CancellationToken.None);

        (await ReadMinuteAsync(start.AddMinutes(2)))!.Value.Count.Should().Be(4);
    }

    [Fact]
    public async Task After_restart_aggregation_resumes_from_the_last_aggregated_minute()
    {
        // 上次运行聚合到了 20 分钟前,之后的几分钟在停机前产生了事件但没来得及聚合
        await SeedEventsAsync(NowUtc.AddMinutes(-20), 1);
        await _events.RebuildMinuteAggregatesAsync(NowUtc.AddMinutes(-20), NowUtc.AddMinutes(-19), CancellationToken.None);
        await SeedEventsAsync(NowUtc.AddMinutes(-15), 6);

        await MakeSut().AggregateAsync(CancellationToken.None);   // 新进程的第一次聚合

        (await ReadMinuteAsync(NowUtc.AddMinutes(-15)))!.Value.Count.Should().Be(6);
    }

    [Fact]
    public async Task Events_persisted_after_their_minute_was_aggregated_are_added_on_the_next_run()
    {
        var sut = MakeSut();
        var prevMinute = NowUtc.AddMinutes(-1);
        await SeedEventsAsync(prevMinute, 3);
        await sut.AggregateAsync(CancellationToken.None);

        await SeedEventsAsync(prevMinute.AddSeconds(30), 2);   // 晚落库
        _clock.Advance(TimeSpan.FromMinutes(1));
        await sut.AggregateAsync(CancellationToken.None);

        (await ReadMinuteAsync(prevMinute))!.Value.Count.Should().Be(5);
    }
}
