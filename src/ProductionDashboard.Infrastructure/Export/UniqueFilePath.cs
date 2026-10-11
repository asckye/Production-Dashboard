using System.IO;

namespace ProductionDashboard.Infrastructure.Export;

/// <summary>
/// 为归档 / 备份文件挑一个还不存在的文件名,保证不覆盖已有文件。
/// <para>
/// 首选 <c>{name}{extension}</c>;已存在时改用 <c>{name}_{yyyyMMdd-HHmmss}{extension}</c>,
/// 同一秒内仍冲突再追加序号 <c>_2</c>、<c>_3</c>……
/// </para>
/// </summary>
internal static class UniqueFilePath
{
    /// <summary>返回 <paramref name="directory"/> 下一个可用的文件路径(不创建文件)。</summary>
    public static string Next(string directory, string name, string extension, DateTime localNow)
    {
        var preferred = Path.Combine(directory, name + extension);
        if (!File.Exists(preferred)) return preferred;

        var stamped = $"{name}_{localNow:yyyyMMdd-HHmmss}";
        var candidate = Path.Combine(directory, stamped + extension);
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{stamped}_{i}{extension}");
        }
        return candidate;
    }
}
