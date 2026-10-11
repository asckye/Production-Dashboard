using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProductionDashboard.Watchdog;

/// <summary>
/// 心跳文件读写 + 序列化。
/// 主程序 UI 线程定期写入 <see cref="Heartbeat"/>,守护进程定期读取。
/// 文件用 JSON 是因为它便于人工排查 (运维直接 cat heartbeat.json 就能看是谁在写)。
/// </summary>
public static class HeartbeatFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 原子地写入心跳。
    /// 先写到 <c>.tmp</c> 再 <see cref="File.Move(string, string, bool)"/> 覆盖,
    /// 避免守护进程读到半写文件 (Windows 上单文件 move 是原子操作)。
    /// </summary>
    public static void Write(string path, Heartbeat heartbeat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(heartbeat);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(heartbeat, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 读取心跳。文件不存在或解析失败时返回 <c>null</c>,由调用方决定是否容忍。
    /// </summary>
    public static Heartbeat? TryRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // 允许读的同时被覆盖:File.ReadAllText 不带 FileShare.Delete,
            // 守护进程读文件的瞬间,主程序 Write 里的 File.Move 覆盖会被拒绝(Access denied)
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Heartbeat>(stream, JsonOptions);
        }
        catch (Exception)
        {
            // 文件被截断 / JSON 格式错误 → 视作无心跳
            return null;
        }
    }
}

/// <summary>
/// 心跳文件的载荷。所有字段都用 <c>init</c>,避免读出来后被意外改回写丢失。
/// </summary>
public sealed record Heartbeat
{
    /// <summary>主程序进程 ID。</summary>
    public required int Pid { get; init; }

    /// <summary>写入时刻 (UTC)。守护用 <c>UtcNow - Timestamp</c> 判定是否过期。</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>主程序版本,便于排查心跳停留是否伴随版本变化。</summary>
    public string? Version { get; init; }

    /// <summary>
    /// 主程序优雅退出标记。<c>true</c> 时守护不要重启 (用户主动 Esc 退出 / Host 正常 Shutdown)。
    /// </summary>
    public bool GracefulExit { get; init; }
}
