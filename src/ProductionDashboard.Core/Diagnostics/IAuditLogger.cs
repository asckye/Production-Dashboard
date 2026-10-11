namespace ProductionDashboard.Core.Diagnostics;

/// <summary>
/// 操作留痕:把系统里能影响生产数据的关键事件写到 AuditLog 表,
/// 便于事后回溯(谁在什么时候做了什么)。
/// </summary>
public interface IAuditLogger
{
    /// <summary>写入一条审计记录。失败时仅记录日志,不抛出。</summary>
    /// <param name="category">类型:ShiftChange / PlcConnection / YearlyArchive / ConfigChange / Exception 等</param>
    /// <param name="detail">详细信息(JSON 或可读文本均可)</param>
    /// <param name="user">操作人(可空,系统自动事件填 null)</param>
    Task WriteAsync(
        string category,
        string detail,
        string? user = null,
        CancellationToken cancellationToken = default);
}
