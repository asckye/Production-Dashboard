using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProductionDashboard.Core.Diagnostics;

/// <inheritdoc cref="IGlobalExceptionHandler"/>
public sealed partial class GlobalExceptionHandler : IGlobalExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>注入日志。</summary>
    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<ExceptionReportedEventArgs>? ExceptionReported;

    /// <inheritdoc/>
    public void Report(Exception exception, string source)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        _logger.LogError(exception, "未处理异常 [{Source}] · {Message}", source, exception.Message);

        var args = new ExceptionReportedEventArgs
        {
            Source = source,
            // UI 显示前脱敏路径,避免大屏泄露部署目录;日志(上一行)与 Exception 对象保留完整原文。
            Message = SanitizeForDisplay(exception.Message),
            Timestamp = DateTimeOffset.Now,
            Exception = exception,
        };

        // 订阅方自己抛异常的话,不能再让它逃逸 — 否则这个全局 handler 就成了下一个全局异常源。
        try
        {
            ExceptionReported?.Invoke(this, args);
        }
        catch (Exception subscriberEx)
        {
            _logger.LogError(subscriberEx,
                "GlobalExceptionHandler 的订阅方自身抛异常 · 原异常 source={Source}", source);
        }
    }

    /// <summary>
    /// 脱敏 UI 显示用的消息:移除 Windows 绝对路径 / UNC 路径,避免大屏 toast 泄露部署目录结构
    /// (例:SQLite 打不开库的错误常含完整 .db 路径)。仅作用于呈现给操作员的文本;
    /// 完整原始消息 + stack 仍通过日志记录,<see cref="ExceptionReportedEventArgs.Exception"/> 也原样保留。
    /// </summary>
    private static string SanitizeForDisplay(string message)
        => string.IsNullOrEmpty(message)
            ? message
            : WindowsPathRegex().Replace(message, "<路径已隐藏>");

    // 匹配盘符路径(C:\...)或 UNC(\\server\...),到下一个空白 / 引号 / 尖括号 / 竖线为止。
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\s""'<>|]*")]
    private static partial Regex WindowsPathRegex();
}
