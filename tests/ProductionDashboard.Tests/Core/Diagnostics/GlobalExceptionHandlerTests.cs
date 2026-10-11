using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProductionDashboard.Core.Diagnostics;

namespace ProductionDashboard.Tests.Core.Diagnostics;

/// <summary>
/// 全局异常处理器 <see cref="GlobalExceptionHandler"/> 的单元测试。
/// 重点验证:事件载荷完整、所有订阅者都被通知、订阅者异常被吞掉(不能让 handler 自己崩)、
/// 参数校验、无订阅者时也不抛异常。
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    private readonly GlobalExceptionHandler _sut =
        new(NullLogger<GlobalExceptionHandler>.Instance);

    /// <summary>Report 应携带 Source / Message / Exception / Timestamp 完整传给订阅者。</summary>
    [Fact]
    public void Report_raises_ExceptionReported_with_full_payload()
    {
        ExceptionReportedEventArgs? captured = null;
        _sut.ExceptionReported += (_, e) => captured = e;

        var ex = new InvalidOperationException("boom");
        _sut.Report(ex, "Test");

        captured.Should().NotBeNull();
        captured!.Source.Should().Be("Test");
        captured.Message.Should().Be("boom");
        captured.Exception.Should().BeSameAs(ex);
        captured.Timestamp.Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromSeconds(2));
    }

    /// <summary>多个订阅者全部都应被调用一次。</summary>
    [Fact]
    public void Report_invokes_every_subscriber()
    {
        var count = 0;
        _sut.ExceptionReported += (_, _) => count++;
        _sut.ExceptionReported += (_, _) => count++;
        _sut.ExceptionReported += (_, _) => count++;

        _sut.Report(new Exception("x"), "Test");

        count.Should().Be(3);
    }

    /// <summary>订阅者自己抛异常时,Report 应吞掉,避免 handler 变成新的异常源。</summary>
    [Fact]
    public void Report_does_not_propagate_subscriber_exceptions()
    {
        _sut.ExceptionReported += (_, _) => throw new InvalidOperationException("subscriber boom");

        var act = () => _sut.Report(new Exception("x"), "Test");

        // SUT 不该把订阅方的异常抛回给调用方,否则它自己就成了下一个全局异常源
        act.Should().NotThrow();
    }

    /// <summary>exception 参数为 null 时应抛 ArgumentNullException(保证契约清晰)。</summary>
    [Fact]
    public void Report_throws_on_null_exception()
    {
        var act = () => _sut.Report(null!, "Test");
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>source 为空串时应抛 ArgumentException(强制调用方填写来源)。</summary>
    [Fact]
    public void Report_throws_on_empty_source()
    {
        var act = () => _sut.Report(new Exception("x"), "");
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>source 全是空白时同样抛 ArgumentException。</summary>
    [Fact]
    public void Report_throws_on_whitespace_source()
    {
        var act = () => _sut.Report(new Exception("x"), "   ");
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>没有订阅者时调用 Report 仍应正常返回,不抛异常。</summary>
    [Fact]
    public void Report_works_without_subscribers()
    {
        var act = () => _sut.Report(new Exception("nobody listening"), "Test");
        act.Should().NotThrow();
    }

    /// <summary>UI 显示消息应脱敏 Windows 绝对路径,但 Exception 对象与日志保留完整原文。</summary>
    [Fact]
    public void Report_sanitizes_windows_paths_for_display_but_keeps_exception_intact()
    {
        ExceptionReportedEventArgs? captured = null;
        _sut.ExceptionReported += (_, e) => captured = e;

        var ex = new InvalidOperationException(@"unable to open database file: D:\App\ProductionDashboard\data.db");
        _sut.Report(ex, "Test");

        captured!.Message.Should().NotContain(@"D:\App\ProductionDashboard", "大屏 toast 不应泄露部署路径");
        captured.Message.Should().Contain("<路径已隐藏>");
        captured.Exception.Message.Should().Contain(@"D:\App\ProductionDashboard\data.db",
            "Exception 对象保留完整原文,供日志排查");
    }

    /// <summary>不含路径的普通消息原样显示,不被脱敏误伤。</summary>
    [Fact]
    public void Report_leaves_path_free_message_unchanged()
    {
        ExceptionReportedEventArgs? captured = null;
        _sut.ExceptionReported += (_, e) => captured = e;

        _sut.Report(new Exception("PLC 连接超时,错误码 -2"), "Test");

        captured!.Message.Should().Be("PLC 连接超时,错误码 -2");
    }
}
