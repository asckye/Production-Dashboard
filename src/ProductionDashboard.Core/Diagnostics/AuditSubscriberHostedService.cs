using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Plc;
using ProductionDashboard.Core.Shifts;

namespace ProductionDashboard.Core.Diagnostics;

/// <summary>
/// 把系统级事件桥接到 <see cref="IAuditLogger"/>。
/// 当前订阅:班次切换(ShiftChanged)+ PLC 连接状态变化(ConnectionStateChanged)。
/// 年度归档由 YearlyArchiveHostedService 自己直接调 audit.WriteAsync 写入。
/// </summary>
public sealed class AuditSubscriberHostedService : IHostedService
{
    private readonly IAuditLogger _audit;
    private readonly IShiftManager _shifts;
    private readonly IPlcService _plc;
    private readonly ILogger<AuditSubscriberHostedService> _logger;

    /// <summary>注入审计 / 班次 / PLC / 日志,订阅 ShiftChanged + ConnectionStateChanged。</summary>
    public AuditSubscriberHostedService(
        IAuditLogger audit,
        IShiftManager shifts,
        IPlcService plc,
        ILogger<AuditSubscriberHostedService> logger)
    {
        _audit = audit;
        _shifts = shifts;
        _plc = plc;
        _logger = logger;

        _shifts.ShiftChanged += OnShiftChanged;
        _plc.ConnectionStateChanged += OnPlcConnectionChanged;
    }

    /// <summary>启动 — 订阅已在构造期完成,这里仅打日志。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AuditSubscriber 启动 · 监听班次切换 + PLC 连接状态");
        return Task.CompletedTask;
    }

    /// <summary>停止 — 取消订阅。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shifts.ShiftChanged -= OnShiftChanged;
        _plc.ConnectionStateChanged -= OnPlcConnectionChanged;
        return Task.CompletedTask;
    }

    private void OnShiftChanged(ShiftAssignment? oldShift, ShiftAssignment? newShift)
    {
        var detail =
            $"{Describe(oldShift)} → {Describe(newShift)}";
        SafeWriteAudit("ShiftChange", detail);
    }

    private void OnPlcConnectionChanged(bool isConnected)
    {
        SafeWriteAudit("PlcConnection", isConnected ? "已连接" : "已断开");
    }

    /// <summary>
    /// fire-and-forget 写 AuditLog,但显式 observe Task 异常防止漏出到
    /// TaskScheduler.UnobservedTaskException(被红条 banner 当作系统异常上报)。
    /// AuditLogger.WriteAsync 内部已有 try/catch,这里是双保险。
    /// </summary>
    private void SafeWriteAudit(string category, string detail)
    {
        _ = _audit.WriteAsync(category, detail, user: null)
            .ContinueWith(
                t =>
                {
                    if (t.Exception is { } ex)
                    {
                        _logger.LogError(ex,
                            "AuditLog 写入未观察异常 · {Category} {Detail}", category, detail);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private static string Describe(ShiftAssignment? a) =>
        a is null ? "(空档)" : $"{a.Definition.Code}·{a.Definition.Name}@{a.ShiftDate:yyyy-MM-dd}";
}
