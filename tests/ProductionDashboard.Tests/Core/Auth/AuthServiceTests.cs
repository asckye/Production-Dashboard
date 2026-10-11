using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Auth;
using ProductionDashboard.Core.Diagnostics;
using ProductionDashboard.Tests.Helpers;

namespace ProductionDashboard.Tests.Core.Auth;

/// <summary>
/// AuthService 测试 — 登录 / 登出 / 空闲超时 / 活动续期。
/// 用 <see cref="ControlledTimeProvider"/> 精确推时间,避免真实 sleep。
/// </summary>
public sealed class AuthServiceTests
{
    private static AuthService MakeService(
        ControlledTimeProvider clock,
        string leaderPassword = "leader",
        int idleTimeoutMinutes = 30,
        StubAuditLogger? audit = null)
    {
        var options = Options.Create(new AuthOptions
        {
            ExitPassword = "1234",
            LeaderPassword = leaderPassword,
            LeaderIdleTimeoutMinutes = idleTimeoutMinutes,
        });
        return new AuthService(
            options,
            audit ?? new StubAuditLogger(),
            NullLogger<AuthService>.Instance,
            clock);
    }

    [Fact]
    public void Login_with_correct_password_succeeds_and_sets_state()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);

        var ok = sut.TryLogin("张三", "leader");

        ok.Should().BeTrue();
        sut.IsAuthenticated.Should().BeTrue();
        sut.CurrentOperator.Should().Be("张三");
    }

    [Fact]
    public void Login_with_wrong_password_fails_and_no_state_change()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);

        var ok = sut.TryLogin("张三", "wrong");

        ok.Should().BeFalse();
        sut.IsAuthenticated.Should().BeFalse();
        sut.CurrentOperator.Should().BeNull();
    }

    [Fact]
    public void Login_with_blank_operator_name_throws()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);

        var act = () => sut.TryLogin("   ", "leader");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Logout_clears_state_and_raises_event()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);
        sut.TryLogin("张三", "leader");
        var changed = 0;
        sut.AuthChanged += () => changed++;

        sut.Logout();

        sut.IsAuthenticated.Should().BeFalse();
        sut.CurrentOperator.Should().BeNull();
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Idle_timeout_logs_out_automatically()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock, idleTimeoutMinutes: 30);
        await sut.StartAsync(CancellationToken.None);
        sut.TryLogin("张三", "leader");
        var changed = 0;
        sut.AuthChanged += () => changed++;

        // 推进 31 分钟,内部 1 分钟检查 timer 应该至少触发一次,并发现超时
        clock.Advance(TimeSpan.FromMinutes(31));

        sut.IsAuthenticated.Should().BeFalse("超过空闲超时阈值后应自动登出");
        changed.Should().BeGreaterThan(0);
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Activity_resets_idle_timer()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock, idleTimeoutMinutes: 30);
        await sut.StartAsync(CancellationToken.None);
        sut.TryLogin("张三", "leader");

        // 25 分钟 → 还没超时
        clock.Advance(TimeSpan.FromMinutes(25));
        sut.IsAuthenticated.Should().BeTrue();

        // 用户产生活动 → 续期
        sut.NotifyActivity();

        // 再过 25 分钟(累计 50min,但活动后 25min 未到 30 阈值)
        clock.Advance(TimeSpan.FromMinutes(25));
        sut.IsAuthenticated.Should().BeTrue("活动后续期 30 分钟,目前才过 25");

        // 再 10 分钟 → 累计 35 分钟无活动 → 超时
        clock.Advance(TimeSpan.FromMinutes(10));
        sut.IsAuthenticated.Should().BeFalse("活动后 35 分钟未续期,应超时登出");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Logout_is_idempotent_when_not_logged_in()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);
        var changed = 0;
        sut.AuthChanged += () => changed++;

        sut.Logout();

        sut.IsAuthenticated.Should().BeFalse();
        changed.Should().Be(0, "本来就没登录,Logout 不应触发事件");
    }

    [Fact]
    public void Login_locks_out_after_5_consecutive_failures_then_recovers_after_timeout()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);

        // 连续 5 次错误口令 → 触发锁定
        for (var i = 0; i < 5; i++)
            sut.TryLogin("张三", "wrong").Should().BeFalse();

        // 锁定期内,即使口令正确也被拒绝
        sut.TryLogin("张三", "leader").Should().BeFalse("连续失败 5 次后应锁定 3 分钟");
        sut.IsAuthenticated.Should().BeFalse();

        // 推进超过 3 分钟锁定期 → 正确口令恢复可登录
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        sut.TryLogin("张三", "leader").Should().BeTrue("锁定期过后应恢复");
        sut.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void VerifyExitPassword_accepts_correct_rejects_wrong_and_null()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock); // ExitPassword = "1234"

        sut.VerifyExitPassword("1234").Should().BeTrue();
        sut.VerifyExitPassword("0000").Should().BeFalse();
        sut.VerifyExitPassword(null!).Should().BeFalse();
    }

    [Fact]
    public void VerifyExitPassword_locks_out_after_5_consecutive_failures()
    {
        var clock = new ControlledTimeProvider();
        var sut = MakeService(clock);

        for (var i = 0; i < 5; i++)
            sut.VerifyExitPassword("bad").Should().BeFalse();

        // 锁定期内即使正确也拒绝
        sut.VerifyExitPassword("1234").Should().BeFalse("退出验证连续失败 5 次后应锁定");

        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        sut.VerifyExitPassword("1234").Should().BeTrue("锁定期过后应恢复");
    }

    private sealed class StubAuditLogger : IAuditLogger
    {
        public Task WriteAsync(string category, string detail, string? user = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
