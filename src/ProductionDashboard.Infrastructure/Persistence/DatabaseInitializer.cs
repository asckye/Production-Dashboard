using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Infrastructure.Persistence.Migrations;

namespace ProductionDashboard.Infrastructure.Persistence;

/// <summary>
/// 启动时打开数据库、设置 PRAGMA、应用未执行的迁移。
/// 注册为 <c>IHostedService</c>,其它依赖数据库的服务都在它之后启动。
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    /// <summary>注入连接工厂 + 日志。</summary>
    public DatabaseInitializer(
        IDbConnectionFactory factory,
        ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>启动 — 设置 PRAGMA,创建 schema_version 表,应用所有未执行的迁移。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("初始化数据库 · 路径 {Path}", _factory.DatabasePath);

        await using var conn = _factory.OpenWrite();

        await ExecAsync(conn, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecAsync(conn, "PRAGMA synchronous=NORMAL;", cancellationToken);
        await ExecAsync(conn, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecAsync(conn, "PRAGMA temp_store=MEMORY;", cancellationToken);
        await ExecAsync(conn, "PRAGMA cache_size=-20000;", cancellationToken); // ~20MB

        await EnsureSchemaVersionTableAsync(conn, cancellationToken);

        var currentVersion = await GetCurrentSchemaVersionAsync(conn, cancellationToken);
        _logger.LogInformation("当前 schema 版本 = {Version}", currentVersion);

        var pending = MigrationCatalog.All
            .Where(m => m.Version > currentVersion)
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("无待执行迁移,数据库已是最新");
            return;
        }

        foreach (var migration in pending)
        {
            await ApplyMigrationAsync(conn, migration, cancellationToken);
        }

        _logger.LogInformation("数据库初始化完成,已应用 {Count} 个迁移", pending.Count);
    }

    /// <summary>停止 — 无后台任务,直接返回。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ApplyMigrationAsync(
        SqliteConnection conn,
        Migration migration,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "应用迁移 v{Version} · {Description}",
            migration.Version, migration.Description);

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var sql in migration.Statements)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var record = conn.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText =
                    "INSERT INTO schema_version (Version, AppliedAt, Description) " +
                    "VALUES (@v, @t, @d);";
                record.Parameters.AddWithValue("@v", migration.Version);
                record.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
                record.Parameters.AddWithValue("@d", migration.Description);
                await record.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "迁移 v{Version} 失败,事务回滚", migration.Version);
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task EnsureSchemaVersionTableAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS schema_version (
                Version     INTEGER NOT NULL PRIMARY KEY,
                AppliedAt   TEXT    NOT NULL,
                Description TEXT    NOT NULL
            );
            """, ct);
    }

    private static async Task<int> GetCurrentSchemaVersionAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM schema_version;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task ExecAsync(
        SqliteConnection conn,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
