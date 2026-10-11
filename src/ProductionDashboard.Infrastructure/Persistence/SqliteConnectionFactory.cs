using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ProductionDashboard.Infrastructure.Persistence;

/// <inheritdoc cref="IDbConnectionFactory"/>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _writeConnectionString;
    private readonly string _readConnectionString;

    /// <inheritdoc/>
    public string DatabasePath { get; }

    /// <summary>从 DatabaseOptions 读取路径,创建目录并预构建读写连接字符串。</summary>
    public SqliteConnectionFactory(IOptions<DatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dbPath = Path.GetFullPath(options.Value.Path);

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = dbPath;

        // 注意:WAL 模式下用 Cache=Private (默认) 比 Shared 更稳。
        // Shared cache 会让多个 connection 共享同一 schema 元数据,
        // 但 WAL 的可见性是 connection 级别的,共享反而引发竞态。
        //
        // ForeignKeys=true 让驱动在每条物理连接 Open 时自动执行 PRAGMA foreign_keys=ON。
        // 这一项是必须的:foreign_keys 是**连接级** PRAGMA,不随数据库文件持久化,
        // 只在 DatabaseInitializer 那一条连接上设一次是无效的 —— 连接池复用/新建的连接
        // 默认 foreign_keys=OFF,否则所有 FOREIGN KEY 约束在运行时形同虚设。
        _writeConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        // 读连接用 ReadOnly:贯彻最小权限,杜绝 OpenRead() 拿到的连接被误用于写。
        // WAL 模式下 ReadOnly 连接能读到最新已提交数据 —— 前提是每次查询都新开连接
        // (打开时即拍当前最新快照),本工厂正是这样用的(调用方 await using 即开即用即弃),
        // 因此不存在"看不到刚 commit 数据"的旧快照问题。
        _readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <inheritdoc/>
    public SqliteConnection OpenWrite()
    {
        var conn = new SqliteConnection(_writeConnectionString);
        conn.Open();
        ApplyConnectionPragmas(conn, readOnly: false);
        return conn;
    }

    /// <inheritdoc/>
    public SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        ApplyConnectionPragmas(conn, readOnly: true);
        return conn;
    }

    /// <summary>
    /// 设置连接级 PRAGMA。这些设置不随数据库文件持久化,连接池复用的连接也只在物理连接
    /// 首次创建时继承,因此每次拿到连接都显式设置最稳。
    /// (journal_mode=WAL 是**数据库级**持久设置,由 <see cref="DatabaseInitializer"/>
    /// 设一次即永久生效,这里不重复设置;foreign_keys 已通过连接字符串 ForeignKeys=true 处理。)
    /// </summary>
    private static void ApplyConnectionPragmas(SqliteConnection conn, bool readOnly)
    {
        using var cmd = conn.CreateCommand();
        // ReadOnly 连接不写库,synchronous 对其无意义,只设读相关的 cache/temp。
        cmd.CommandText = readOnly
            ? "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000;"
            : "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000;";
        cmd.ExecuteNonQuery();
    }
}
