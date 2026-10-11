using Dapper;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;

namespace ProductionDashboard.Infrastructure.Persistence;

/// <inheritdoc cref="IAuditLogger"/>
public sealed class AuditLogger : IAuditLogger
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<AuditLogger> _logger;

    /// <summary>注入连接工厂 + 日志。</summary>
    public AuditLogger(IDbConnectionFactory factory, ILogger<AuditLogger> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(
        string category,
        string detail,
        string? user = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO AuditLog (Timestamp, Category, Detail, User)
            VALUES (@Timestamp, @Category, @Detail, @User);
            """;

        try
        {
            await using var conn = _factory.OpenWrite();
            await conn.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Category = category,
                    Detail = detail,
                    User = user,
                },
                cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            // 审计写入失败不应该阻塞业务流程
            _logger.LogError(ex,
                "写入 AuditLog 失败 · {Category} {Detail}", category, detail);
        }
    }
}
