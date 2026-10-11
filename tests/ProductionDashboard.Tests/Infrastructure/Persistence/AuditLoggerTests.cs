using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Tests.Infrastructure.Persistence;

/// <summary>
/// AuditLogger 集成测试。用临时 SQLite 文件验证写入 / null 用户 / 多次写入顺序保持。
/// </summary>
public sealed class AuditLoggerTests : IAsyncLifetime
{
    // 每个测试独立 db 文件,避免并行/串行时互相污染
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pdaudit_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;
    private AuditLogger _sut = null!;

    /// <summary>建库 + 跑迁移 + 构造被测对象。</summary>
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
        _sut = new AuditLogger(_factory, NullLogger<AuditLogger>.Instance);
    }

    /// <summary>
    /// 清理临时 db 文件(含 WAL/SHM 边带文件)。
    /// 先 ClearAllPools 释放底层句柄,否则 Windows 下文件占用无法删除。
    /// </summary>
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
        catch { /* 测试清理失败不影响结果 */ }
        return Task.CompletedTask;
    }

    /// <summary>WriteAsync 应把 Category / Detail / User 三个字段完整写入 AuditLog 表。</summary>
    [Fact]
    public async Task WriteAsync_inserts_row_with_category_detail_user()
    {
        await _sut.WriteAsync("ShiftChange", "A → B", user: "operator1");

        await using var conn = _factory.OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Category, Detail, User FROM AuditLog";
        using var reader = await cmd.ExecuteReaderAsync();
        var rowFound = await reader.ReadAsync();
        rowFound.Should().BeTrue();
        reader.GetString(0).Should().Be("ShiftChange");
        reader.GetString(1).Should().Be("A → B");
        reader.GetString(2).Should().Be("operator1");
    }

    /// <summary>系统事件无操作人时 User 字段应允许为 null,DB 落 NULL。</summary>
    [Fact]
    public async Task WriteAsync_allows_null_user_for_system_events()
    {
        await _sut.WriteAsync("PlcConnection", "已连接");

        await using var conn = _factory.OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT User FROM AuditLog";
        var result = await cmd.ExecuteScalarAsync();
        result.Should().BeOfType<DBNull>();
    }

    /// <summary>多次写入按 Id 顺序读取应保留写入顺序(AUTOINCREMENT 主键)。</summary>
    [Fact]
    public async Task WriteAsync_multiple_writes_preserve_order_by_id()
    {
        for (var i = 0; i < 5; i++)
        {
            await _sut.WriteAsync("TestEvent", $"event-{i}");
        }

        await using var conn = _factory.OpenRead();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Detail FROM AuditLog ORDER BY Id";
        using var reader = await cmd.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(0));

        details.Should().Equal("event-0", "event-1", "event-2", "event-3", "event-4");
    }
}
