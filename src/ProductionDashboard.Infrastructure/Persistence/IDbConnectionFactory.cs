using Microsoft.Data.Sqlite;

namespace ProductionDashboard.Infrastructure.Persistence;

/// <summary>
/// 提供已打开的 SQLite 连接。
/// 读写连接和只读连接分开,避免读路径长时间持有写锁。
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>主数据库文件绝对路径。</summary>
    string DatabasePath { get; }

    /// <summary>打开可读写连接(已 Open())。</summary>
    SqliteConnection OpenWrite();

    /// <summary>打开只读连接(已 Open())。</summary>
    SqliteConnection OpenRead();
}
