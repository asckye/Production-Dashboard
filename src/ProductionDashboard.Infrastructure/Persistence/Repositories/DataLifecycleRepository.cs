using Dapper;
using ProductionDashboard.Core.Production;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IDataLifecycleRepository"/>
public sealed class DataLifecycleRepository : IDataLifecycleRepository
{
    private readonly IDbConnectionFactory _factory;

    /// <summary>注入连接工厂(读 / 写连接由 factory 决定 readonly/journal 模式)。</summary>
    public DataLifecycleRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>每批删除行数。年度数据可达数百万行,分批避免单事务长时间独占写锁 + WAL 暴涨。</summary>
    private const int DeleteBatchSize = 10_000;

    /// <inheritdoc/>
    public async Task<int> DeleteProductionDataInRangeAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        // 分批删除(每批一个短事务),而非"三表单事务删整年"。
        // 取舍:放弃了跨三表的单事务原子性,换取短锁 + WAL 受控。这是安全的 ——
        // 调用方(YearlyArchiveHostedService)在清库前已强制整库热备份,且删除幂等
        // (中途失败下次归档会继续删剩余),不存在数据丢失风险。
        var p = new
        {
            from = fromDate.ToString("yyyy-MM-dd"),
            to = toDate.ToString("yyyy-MM-dd"),
        };

        await using var conn = _factory.OpenWrite();

        var total = 0;
        // 表名是代码内常量(非用户输入),内插安全。
        foreach (var table in new[] { "ProductionEvent", "ProductionMinute", "ShiftSummary" })
        {
            total += await DeleteTableBatchedAsync(conn, table, p, cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<int> DeleteTableBatchedAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, string table, object p, CancellationToken ct)
    {
        var sql = $"""
            DELETE FROM {table}
            WHERE rowid IN (
                SELECT rowid FROM {table}
                WHERE ShiftDate >= @from AND ShiftDate <= @to
                LIMIT {DeleteBatchSize}
            );
            """;

        var total = 0;
        while (true)
        {
            var deleted = await conn.ExecuteAsync(
                new CommandDefinition(sql, p, cancellationToken: ct)).ConfigureAwait(false);
            total += deleted;
            if (deleted < DeleteBatchSize) break;     // 不足一批 → 已删完
            await Task.Delay(50, ct).ConfigureAwait(false);  // 让在途写入有机会插队,避免饿死采集
        }
        return total;
    }
}
