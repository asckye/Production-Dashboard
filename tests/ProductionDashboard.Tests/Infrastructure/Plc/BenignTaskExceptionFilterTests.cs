using System.IO;
using System.Net.Sockets;
using FluentAssertions;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Tests.Infrastructure.Plc;

/// <summary>
/// <see cref="BenignTaskExceptionFilter"/> 用于把 Sharp7 重连阶段泄露的 socket-abort
/// Task 静默掉。这套测试要保证两件事:良性 socket abort 被识别;但凡混入一点真异常,
/// 就不能再误判成良性,否则真正的故障会被吞掉。
/// </summary>
public sealed class BenignTaskExceptionFilterTests
{
    /// <summary>Task.Exception 一律包成 AggregateException — 内部纯 OperationAborted 应被识别。</summary>
    [Fact]
    public void Recognizes_aggregate_wrapping_OperationAborted()
    {
        var inner = new SocketException((int)SocketError.OperationAborted);
        var agg = new AggregateException(inner);

        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeTrue();
    }

    /// <summary>Interrupted 与 OperationAborted 同源,也应识别。</summary>
    [Fact]
    public void Recognizes_aggregate_wrapping_Interrupted()
    {
        var inner = new SocketException((int)SocketError.Interrupted);
        var agg = new AggregateException(inner);

        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeTrue();
    }

    /// <summary>裸 SocketException(OperationAborted) 不经 Aggregate 包装时也算良性。</summary>
    [Fact]
    public void Recognizes_bare_socket_exception()
    {
        var ex = new SocketException((int)SocketError.OperationAborted);
        BenignTaskExceptionFilter.IsBenignSocketAbort(ex).Should().BeTrue();
    }

    /// <summary>IOException 包 SocketException(OperationAborted) 是 .NET 网络栈常见包装,也应识别。</summary>
    [Fact]
    public void Recognizes_io_wrapped_socket_abort()
    {
        var inner = new SocketException((int)SocketError.OperationAborted);
        var io = new IOException("aborted", inner);
        var agg = new AggregateException(io);

        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeTrue();
    }

    /// <summary>其他 SocketError(例如连接被重置)绝不能当良性,这是真故障。</summary>
    [Fact]
    public void Rejects_other_socket_errors()
    {
        var inner = new SocketException((int)SocketError.ConnectionReset);
        var agg = new AggregateException(inner);

        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeFalse();
    }

    /// <summary>完全无关的异常(NullRef / InvalidOperation)绝不当良性。</summary>
    [Fact]
    public void Rejects_unrelated_exception()
    {
        var agg = new AggregateException(new InvalidOperationException("real bug"));
        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeFalse();
    }

    /// <summary>混合 inner — 只要混入一个真异常,整体不能放过。</summary>
    [Fact]
    public void Rejects_aggregate_mixing_real_and_benign()
    {
        var benign = new SocketException((int)SocketError.OperationAborted);
        var real = new InvalidOperationException("real");
        var agg = new AggregateException(benign, real);

        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeFalse();
    }

    /// <summary>多个良性 inner — 整体仍算良性(Sharp7 重连风暴时可能两条同时被 GC)。</summary>
    [Fact]
    public void Recognizes_aggregate_of_only_benign_inners()
    {
        var a = new SocketException((int)SocketError.OperationAborted);
        var b = new SocketException((int)SocketError.Interrupted);
        var agg = new AggregateException(a, b);

        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeTrue();
    }

    /// <summary>嵌套 AggregateException 应被 Flatten 后判定 — 不应被嵌套结构绕过。</summary>
    [Fact]
    public void Flattens_nested_aggregate_before_judging()
    {
        var leaf = new SocketException((int)SocketError.OperationAborted);
        var inner = new AggregateException(leaf);
        var outer = new AggregateException(inner);

        BenignTaskExceptionFilter.IsBenignSocketAbort(outer).Should().BeTrue();
    }

    /// <summary>null 参数返回 false — 调用方不需要再额外 null-check。</summary>
    [Fact]
    public void Null_exception_returns_false()
    {
        BenignTaskExceptionFilter.IsBenignSocketAbort(null).Should().BeFalse();
    }

    /// <summary>空 AggregateException 返回 false — 没有 inner 就没有"良性"可言。</summary>
    [Fact]
    public void Empty_aggregate_returns_false()
    {
        var agg = new AggregateException();
        BenignTaskExceptionFilter.IsBenignSocketAbort(agg).Should().BeFalse();
    }
}
