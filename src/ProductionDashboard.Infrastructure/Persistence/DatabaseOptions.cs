using System.ComponentModel.DataAnnotations;

namespace ProductionDashboard.Infrastructure.Persistence;

/// <summary>
/// SQLite 数据库配置。绑定 <c>Database</c> 节点。
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>配置节点名,对应 appsettings.json 中的 "Database" 段。</summary>
    public const string SectionName = "Database";

    /// <summary>主数据库文件路径(相对或绝对)。</summary>
    [Required(AllowEmptyStrings = false)]
    public string Path { get; init; } = "Data/production.db";

    /// <summary>备份目录路径。</summary>
    [Required(AllowEmptyStrings = false)]
    public string BackupPath { get; init; } = "Data/Backups";

    /// <summary>备份文件保留天数。</summary>
    [Range(1, 365)]
    public int BackupRetentionDays { get; init; } = 7;

    /// <summary>每日 VACUUM 触发的时刻 (HH:mm,本地时区)。</summary>
    [Required]
    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$",
        ErrorMessage = "VacuumTime must be HH:mm (24-hour).")]
    public string VacuumTime { get; init; } = "02:00";

    /// <summary>每日备份触发的时刻 (HH:mm,本地时区)。</summary>
    [Required]
    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$",
        ErrorMessage = "BackupTime must be HH:mm (24-hour).")]
    public string BackupTime { get; init; } = "03:00";
}
