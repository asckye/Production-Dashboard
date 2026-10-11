using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Infrastructure.Maintenance;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Tests.Infrastructure.Maintenance;

/// <summary>
/// 数据库整理(VACUUM)的调度:VACUUM 会独占锁库,计数写入只能等待,
/// 所以只能在计划时段内、确有空闲页时做,而且程序重启后不能在同一天再做一次。
/// </summary>
public sealed class DatabaseMaintenanceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pddm_{Guid.NewGuid():N}");
    private SqliteConnectionFactory _factory = null!;
    private IOptions<DatabaseOptions> _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _options = Options.Create(new DatabaseOptions
        {
            Path = Path.Combine(_dir, "production.db"),
            BackupPath = Path.Combine(_dir, "Backups"),
            VacuumTime = "02:00",
            BackupTime = "03:00",
        });
        _factory = new SqliteConnectionFactory(_options);
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance)
            .StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 尽力清理 */ }
        return Task.CompletedTask;
    }

    private DatabaseMaintenanceHostedService MakeSut(int hour, int minute) => new(
        _factory, _options, NullLogger<DatabaseMaintenanceHostedService>.Instance,
        new LocalClock(new DateTime(2026, 10, 11, hour, minute, 0)));

    /// <summary>写入再删除一批数据,制造大量空闲页。</summary>
    private async Task CreateFreePagesAsync()
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Filler (Data BLOB);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
            INSERT INTO Filler SELECT randomblob(2000) FROM n;
            DELETE FROM Filler;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> FreePagesAsync()
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA freelist_count;";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Vacuums_during_the_scheduled_window_when_there_are_many_free_pages()
    {
        await CreateFreePagesAsync();

        await MakeSut(2, 30).CheckAndRunAsync(CancellationToken.None);

        (await FreePagesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Does_not_vacuum_outside_the_window_after_a_daytime_restart()
    {
        await CreateFreePagesAsync();

        await MakeSut(9, 0).CheckAndRunAsync(CancellationToken.None);

        (await FreePagesAsync()).Should().BeGreaterThan(0, "白天(生产期间)不能锁库做 VACUUM");
    }

    [Fact]
    public async Task Does_not_vacuum_twice_on_the_same_day_across_a_restart()
    {
        await MakeSut(2, 10).CheckAndRunAsync(CancellationToken.None);   // 今天已整理过
        await CreateFreePagesAsync();

        await MakeSut(2, 40).CheckAndRunAsync(CancellationToken.None);   // 重启后的新实例,仍在时段内

        (await FreePagesAsync()).Should().BeGreaterThan(0, "今天已经做过,重启后不应再做");
    }

    /// <summary>本地时区 = UTC 的固定时钟,方便直接写本地时刻。</summary>
    private sealed class LocalClock(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(local, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
