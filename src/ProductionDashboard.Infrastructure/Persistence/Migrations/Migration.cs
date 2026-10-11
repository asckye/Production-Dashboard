namespace ProductionDashboard.Infrastructure.Persistence.Migrations;

/// <summary>
/// 一次数据库迁移定义。<see cref="Statements"/> 中的每条 SQL 按顺序在同一事务中执行。
/// </summary>
/// <param name="Version">迁移版本号,严格递增。已应用记录在 schema_migrations 表。</param>
/// <param name="Description">人类可读的简短说明,用于日志和 schema_migrations 表。</param>
/// <param name="Statements">本次迁移要执行的 SQL 语句序列;同一事务内按顺序执行,任一失败整笔回滚。</param>
public sealed record Migration(
    int Version,
    string Description,
    IReadOnlyList<string> Statements);
