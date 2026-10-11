using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductionDashboard.Core.Diagnostics;

namespace ProductionDashboard.Core.Auth;

/// <inheritdoc cref="IAuthService"/>
public sealed class AuthService : IAuthService, IHostedService, IAsyncDisposable
{
    // 暴力破解防护:连续失败 MaxFailedAttempts 次锁定 LockoutDuration。
    // 登录与退出各自独立计数,互不影响。
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(3);

    // 已知弱默认值 —— 仅供开发,生产必须用环境变量 Display__LeaderPassword / Display__ExitPassword 覆盖。
    private const string DefaultLeaderPassword = "leader";
    private const string DefaultExitPassword = "1234";

    private readonly AuthOptions _options;
    private readonly IAuditLogger _audit;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private string? _currentOperator;
    private DateTime _lastActivityUtc;
    private ITimer? _idleTimer;
    private int _loginFailures;
    private DateTime _loginLockedUntilUtc = DateTime.MinValue;
    private int _exitFailures;
    private DateTime _exitLockedUntilUtc = DateTime.MinValue;
    private bool _stopped;
    private bool _disposed;

    /// <summary>注入选项 / 审计 / 日志,<paramref name="clock"/> 可注入测试时钟。</summary>
    public AuthService(
        IOptions<AuthOptions> options,
        IAuditLogger audit,
        ILogger<AuthService> logger,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string? CurrentOperator { get { lock (_gate) return _currentOperator; } }

    /// <inheritdoc/>
    public bool IsAuthenticated { get { lock (_gate) return _currentOperator is not null; } }

    /// <inheritdoc/>
    public event Action? AuthChanged;

    /// <inheritdoc/>
    public bool TryLogin(string operatorName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        if (password is null) return false;

        // 锁定窗口内直接拒绝(防暴力破解)
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (now < _loginLockedUntilUtc)
            {
                var remain = (_loginLockedUntilUtc - now).TotalSeconds;
                _logger.LogWarning(
                    "班长登录被锁定 · 操作员 {Operator} · 还剩 {Sec:F0}s", operatorName, remain);
                _ = _audit.WriteAsync("Auth", $"login_locked by {operatorName}", operatorName);
                return false;
            }
        }

        if (!FixedTimeEquals(password, _options.LeaderPassword))
        {
            int failures;
            lock (_gate)
            {
                failures = ++_loginFailures;
                if (failures >= MaxFailedAttempts)
                {
                    _loginLockedUntilUtc = _clock.GetUtcNow().UtcDateTime.Add(LockoutDuration);
                    _loginFailures = 0;
                }
            }
            _logger.LogWarning(
                "班长登录失败 · 操作员 {Operator} 口令错误 · 累计失败 {Failures}", operatorName, failures);
            _ = _audit.WriteAsync("Auth", $"login_fail by {operatorName} (attempt {failures})", operatorName);
            if (failures >= MaxFailedAttempts)
            {
                _logger.LogWarning("班长登录连续失败 {Max} 次,锁定 {Min} 分钟", MaxFailedAttempts, LockoutDuration.TotalMinutes);
            }
            return false;
        }

        lock (_gate)
        {
            _loginFailures = 0;
            _loginLockedUntilUtc = DateTime.MinValue;
            _currentOperator = operatorName.Trim();
            _lastActivityUtc = _clock.GetUtcNow().UtcDateTime;
        }

        // 审计 — Fire and forget,失败不影响登录态
        _ = _audit.WriteAsync("Auth", $"login by {operatorName}", operatorName);

        _logger.LogInformation("班长登录成功 · 操作员 {Operator}", operatorName);
        RaiseChanged();
        return true;
    }

    /// <inheritdoc/>
    public bool VerifyExitPassword(string password)
    {
        if (password is null) return false;

        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (now < _exitLockedUntilUtc)
            {
                _logger.LogWarning("退出验证被锁定 · 还剩 {Sec:F0}s", (_exitLockedUntilUtc - now).TotalSeconds);
                _ = _audit.WriteAsync("Auth", "exit_locked");
                return false;
            }
        }

        if (FixedTimeEquals(password, _options.ExitPassword))
        {
            lock (_gate)
            {
                _exitFailures = 0;
                _exitLockedUntilUtc = DateTime.MinValue;
            }
            _ = _audit.WriteAsync("Auth", "exit_unlocked");
            _logger.LogInformation("退出大屏密码验证通过");
            return true;
        }

        int failures;
        lock (_gate)
        {
            failures = ++_exitFailures;
            if (failures >= MaxFailedAttempts)
            {
                _exitLockedUntilUtc = _clock.GetUtcNow().UtcDateTime.Add(LockoutDuration);
                _exitFailures = 0;
            }
        }
        _logger.LogWarning("退出大屏密码错误 · 累计失败 {Failures}", failures);
        _ = _audit.WriteAsync("Auth", $"exit_denied (attempt {failures})");
        return false;
    }

    /// <inheritdoc/>
    public void Logout() => LogoutInternal(reason: "manual");

    /// <inheritdoc/>
    public void NotifyActivity()
    {
        lock (_gate)
        {
            if (_currentOperator is null) return;
            _lastActivityUtc = _clock.GetUtcNow().UtcDateTime;
        }
    }

    /// <summary>启动 — 起 1 分钟一次的 idle 检测 timer。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 1 分钟粒度足够 — 闲置超时本身就是分钟级
        var period = TimeSpan.FromMinutes(1);
        _idleTimer = _clock.CreateTimer(_ => CheckIdleTimeout(), state: null, period, period);
        _logger.LogInformation(
            "AuthService 启动 · 空闲超时 {Minutes} 分钟", _options.LeaderIdleTimeoutMinutes);

        // 安全告警:检测到弱默认密码 —— 不阻止启动(否则开发期 dotnet run 受阻),
        // 但必须高声警告,提示生产部署用环境变量覆盖。
        if (FixedTimeEquals(_options.LeaderPassword, DefaultLeaderPassword)
            || FixedTimeEquals(_options.ExitPassword, DefaultExitPassword))
        {
            _logger.LogWarning(
                "⚠ 正在使用默认密码(ExitPassword/LeaderPassword)。生产部署务必通过环境变量 " +
                "Display__ExitPassword / Display__LeaderPassword 覆盖为强口令,切勿沿用默认值。");
        }

        return Task.CompletedTask;
    }

    /// <summary>停止 — dispose idle timer,幂等。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;
        if (_idleTimer is not null)
        {
            await _idleTimer.DisposeAsync();
            _idleTimer = null;
        }
    }

    /// <summary>异步释放 — 委派给 StopAsync,幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void CheckIdleTimeout()
    {
        string? toLogout = null;
        lock (_gate)
        {
            if (_currentOperator is null) return;
            var idle = _clock.GetUtcNow().UtcDateTime - _lastActivityUtc;
            if (idle.TotalMinutes >= _options.LeaderIdleTimeoutMinutes)
            {
                toLogout = _currentOperator;
            }
        }

        if (toLogout is not null)
        {
            _logger.LogInformation("班长空闲超时自动登出 · 操作员 {Operator}", toLogout);
            LogoutInternal(reason: "idle-timeout");
        }
    }

    private void LogoutInternal(string reason)
    {
        string? wasOperator;
        lock (_gate)
        {
            wasOperator = _currentOperator;
            _currentOperator = null;
        }
        if (wasOperator is null) return;     // 本来就没登录,什么也不做

        _ = _audit.WriteAsync("Auth", $"logout ({reason}) for {wasOperator}", wasOperator);
        _logger.LogInformation("班长登出 · 操作员 {Operator} · 原因 {Reason}", wasOperator, reason);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { AuthChanged?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "AuthChanged 订阅方异常"); }
    }

    /// <summary>
    /// 恒定时间字符串比对(防时序侧信道)。长度不同直接返回 false(由 FixedTimeEquals 保证)。
    /// 明文共享口令场景下时序攻击风险低,但比对几乎零成本,顺手做对。
    /// </summary>
    private static bool FixedTimeEquals(string? a, string? b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bb = Encoding.UTF8.GetBytes(b ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
