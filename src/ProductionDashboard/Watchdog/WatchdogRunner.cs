using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ProductionDashboard.Watchdog;

/// <summary>
/// 守护进程主循环。被同一个 exe 用 <c>--watchdog ...</c> 子进程启动后运行。
/// 职责单一:监视心跳文件 + 父进程 PID,过期就杀 + 重启。
/// </summary>
public static class WatchdogRunner
{
    /// <summary>
    /// 守护进程入口。参数:
    /// <c>--pid=N --heartbeat=PATH --restart-exe=PATH --timeout-ms=N --poll-ms=N --restart-delay-ms=N</c>
    /// </summary>
    public static int Run(string[] args)
    {
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            WriteLog("watchdog 参数缺失,放弃启动 · args=" + string.Join(' ', args));
            return 2;
        }

        WriteLog($"watchdog 启动 · pid={parsed.ParentPid} heartbeat={parsed.HeartbeatPath} " +
                 $"timeout={parsed.TimeoutMs}ms poll={parsed.PollIntervalMs}ms");

        try
        {
            return Loop(parsed);
        }
        catch (Exception ex)
        {
            WriteLog($"watchdog 致命异常 · {ex}");
            return 3;
        }
    }

    private static int Loop(WatchdogArgs args)
    {
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(args.ParentPid);
        }
        catch (ArgumentException)
        {
            WriteLog($"watchdog: 主程序 pid {args.ParentPid} 启动前已退出,放弃守护");
            return 0;
        }

        // 判断心跳是否过期用单调时钟:记录"心跳时间戳最后一次变化"的时刻,
        // 而不是拿系统时间减去文件里的时间戳 —— 校时 / 手动改系统时间会让后者误判。
        // 一直读不到心跳文件也算没有进展,超时后同样重启。
        DateTimeOffset? lastStamp = null;
        var lastProgressAt = Environment.TickCount64;

        while (true)
        {
            Thread.Sleep(args.PollIntervalMs);

            if (parent.HasExited)
            {
                WriteLog($"watchdog: 主程序自然退出 (exit code {SafeExitCode(parent)}),根据心跳判断是否重启");
                if (ShouldRestart(args))
                {
                    LaunchMain(args);
                }
                return 0;
            }

            var hb = HeartbeatFile.TryRead(args.HeartbeatPath);
            if (hb is not null)
            {
                if (hb.GracefulExit)
                {
                    WriteLog("watchdog: 主程序已置 gracefulExit,守护退出");
                    return 0;
                }

                if (hb.Timestamp != lastStamp)
                {
                    lastStamp = hb.Timestamp;
                    lastProgressAt = Environment.TickCount64;
                }
            }

            var stalledMs = Environment.TickCount64 - lastProgressAt;
            if (stalledMs > args.TimeoutMs)
            {
                WriteLog($"watchdog: 心跳已 {stalledMs / 1000.0:F1}s 未更新 > {args.TimeoutMs / 1000.0:F1}s,杀进程重启");
                return KillAndRestart(parent, args) ? 0 : 4;
            }
        }
    }

    private static bool ShouldRestart(WatchdogArgs args)
    {
        var hb = HeartbeatFile.TryRead(args.HeartbeatPath);
        // 没有心跳文件 → 主程序在写心跳前就崩了 → 应该重启
        // 有心跳但 gracefulExit=true → 用户主动退出 → 不重启
        // 有心跳但过期 → 异常退出 → 重启
        if (hb is null) return true;
        return !hb.GracefulExit;
    }

    /// <summary>
    /// 杀掉卡死的主程序并重新拉起。只杀主程序本身:守护进程是主程序的子进程,
    /// <c>Kill(entireProcessTree: true)</c> 会因为进程树里包含调用者自己而直接抛异常。
    /// 主程序确认退出后才重启,避免同时跑两个实例。返回是否已重启。
    /// </summary>
    private static bool KillAndRestart(Process parent, WatchdogArgs args)
    {
        try
        {
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: false);
            }
            if (!parent.WaitForExit(10_000))
            {
                WriteLog($"watchdog: 主程序 pid {args.ParentPid} 10s 内未退出,放弃重启");
                return false;
            }
        }
        catch (Exception ex)
        {
            WriteLog($"watchdog: kill 失败,放弃重启 · {ex.Message}");
            return false;
        }

        Thread.Sleep(args.RestartDelayMs);
        LaunchMain(args);
        return true;
    }

    private static void LaunchMain(WatchdogArgs args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = args.RestartExePath,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(args.RestartExePath) ?? string.Empty,
            };
            var child = Process.Start(psi);
            WriteLog($"watchdog: 已重启主程序 · pid={child?.Id}");
        }
        catch (Exception ex)
        {
            WriteLog($"watchdog: 重启失败 · {ex}");
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    // ── 参数解析 ─────────────────────────────────────────────────────

    private sealed record WatchdogArgs(
        int ParentPid,
        string HeartbeatPath,
        string RestartExePath,
        int TimeoutMs,
        int PollIntervalMs,
        int RestartDelayMs);

    private static WatchdogArgs? ParseArgs(string[] args)
    {
        int? pid = null;
        string? hbPath = null;
        string? exePath = null;
        var timeout = 60_000;
        var poll = 5000;
        var delay = 3000;

        foreach (var raw in args)
        {
            if (!raw.StartsWith("--", StringComparison.Ordinal)) continue;
            var eq = raw.IndexOf('=');
            if (eq < 0) continue;
            var key = raw[2..eq];
            var value = raw[(eq + 1)..];

            switch (key)
            {
                case "pid":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var p)) pid = p;
                    break;
                case "heartbeat":
                    hbPath = value;
                    break;
                case "restart-exe":
                    exePath = value;
                    break;
                case "timeout-ms":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var t)) timeout = t;
                    break;
                case "poll-ms":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var pl)) poll = pl;
                    break;
                case "restart-delay-ms":
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var d)) delay = d;
                    break;
            }
        }

        if (pid is null || string.IsNullOrEmpty(hbPath) || string.IsNullOrEmpty(exePath))
        {
            return null;
        }
        return new WatchdogArgs(pid.Value, hbPath, exePath, timeout, poll, delay);
    }

    // ── 守护进程的日志 (Serilog 不在这条独立链路里,自己写一个低成本 sink) ──

    private static readonly Lock LogLock = new();

    private static void WriteLog(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [WATCHDOG] {message}";
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // 子进程可能没有 stdout,忽略
        }

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"watchdog-{DateTime.Now:yyyy-MM-dd}.log");
            lock (LogLock)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // 文件系统失败也只能放弃记日志,不能再抛
        }
    }
}
