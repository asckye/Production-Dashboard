using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using ProductionDashboard.Configuration;
using ProductionDashboard.Infrastructure.Plc;

namespace ProductionDashboard.Tests.Configuration;

/// <summary>
/// Options 跨字段校验(IValidatableObject)测试。验证那些单字段 DataAnnotation 表达不了的
/// 条件约束在 ValidateOnStart 期能被拦下。
/// </summary>
public sealed class OptionsValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(IValidatableObject o)
        => o.Validate(new ValidationContext(o)).ToList();

    [Fact]
    public void PlcOptions_handshake_without_seq_address_is_invalid()
    {
        var opts = new PlcOptions { SampleMode = PlcSampleMode.Handshake, CompleteSeqAddress = null };

        Validate(opts).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(PlcOptions.CompleteSeqAddress));
    }

    [Fact]
    public void PlcOptions_handshake_with_seq_address_is_valid()
    {
        var opts = new PlcOptions { SampleMode = PlcSampleMode.Handshake, CompleteSeqAddress = "DB100.DBD2" };

        Validate(opts).Should().BeEmpty();
    }

    [Fact]
    public void PlcOptions_edge_without_seq_address_is_valid()
    {
        var opts = new PlcOptions { SampleMode = PlcSampleMode.Edge, CompleteSeqAddress = null };

        Validate(opts).Should().BeEmpty("Edge 模式不需要序号字");
    }

    [Fact]
    public void WatchdogOptions_timeout_below_3x_interval_is_invalid()
    {
        // 30000 < 3 × 20000 = 60000 → 不合法
        var opts = new WatchdogOptions { HeartbeatIntervalMs = 20_000, HeartbeatTimeoutMs = 30_000 };

        Validate(opts).Should().ContainSingle();
    }

    [Fact]
    public void WatchdogOptions_timeout_at_least_3x_interval_is_valid()
    {
        // 60000 = 3 × 20000 → 合法(默认配置)
        var opts = new WatchdogOptions { HeartbeatIntervalMs = 5_000, HeartbeatTimeoutMs = 60_000 };

        Validate(opts).Should().BeEmpty();
    }
}
