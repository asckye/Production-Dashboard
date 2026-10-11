using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;

namespace ProductionDashboard.Core.Shifts;

/// <inheritdoc cref="ITeamAssignmentService"/>
public sealed class TeamAssignmentService : ITeamAssignmentService, IHostedService
{
    private readonly ITeamRepository _teamRepo;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TeamAssignmentService> _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private IReadOnlyList<Team> _teams = Array.Empty<Team>();
    private Dictionary<int, Team> _assignments = new();

    /// <summary>注入仓库 / 审计 / 日志,<paramref name="clock"/> 可注入测试时钟。</summary>
    public TeamAssignmentService(
        ITeamRepository teamRepo,
        IAuditLogger audit,
        ILogger<TeamAssignmentService> logger,
        TimeProvider? clock = null)
    {
        _teamRepo = teamRepo;
        _audit = audit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Team> Teams
    {
        get { lock (_gate) return _teams; }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Team> CurrentAssignments
    {
        get { lock (_gate) return _assignments; }
    }

    /// <inheritdoc/>
    public Team? GetTeamForSlot(int shiftDefId)
    {
        lock (_gate) return _assignments.TryGetValue(shiftDefId, out var t) ? t : null;
    }

    /// <inheritdoc/>
    public event Action? AssignmentsChanged;

    /// <summary>启动 — 从 DB 加载激活班组 + 当前槽位分配。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        _logger.LogInformation(
            "TeamAssignmentService 启动 · 加载 {Teams} 个班组 · {Assignments} 个槽位分配",
            Teams.Count, CurrentAssignments.Count);
    }

    /// <summary>停止 — 无后台 timer,直接返回。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var teams = await _teamRepo.GetActiveTeamsAsync(cancellationToken);
        var assignments = await _teamRepo.GetCurrentAssignmentsAsync(cancellationToken);

        // assignment.TeamId 可能引用了已停用班组(IsActive=false),那种情况下视为无效分配,
        // GetTeamForSlot 会返回 null,UI 会显示"未分配"
        var teamsById = teams.ToDictionary(t => t.Id);
        var fresh = new Dictionary<int, Team>(capacity: assignments.Count);
        foreach (var a in assignments)
        {
            if (teamsById.TryGetValue(a.TeamId, out var team))
            {
                fresh[a.ShiftDefId] = team;
            }
            else
            {
                _logger.LogWarning(
                    "槽位 {ShiftDefId} 分配到的班组 Id={TeamId} 已停用,该槽位将显示为未分配",
                    a.ShiftDefId, a.TeamId);
            }
        }

        lock (_gate)
        {
            _teams = teams;
            _assignments = fresh;
        }
        RaiseChanged();
    }

    /// <inheritdoc/>
    public async Task SwapTwoSlotsAsync(string operatorName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        // 取当前所有分配的快照 — 必须严格 2 个,否则"对调"语义不存在
        var current = await _teamRepo.GetCurrentAssignmentsAsync(cancellationToken);
        if (current.Count != 2)
        {
            throw new InvalidOperationException(
                $"倒班一键对调仅在 2 个槽位场景下可用,当前共 {current.Count} 个槽位分配。请用「逐槽位重新分配」。");
        }

        // 备份用于审计
        var before = current.ToDictionary(a => a.ShiftDefId, a => a.TeamId);
        var slotA = current[0].ShiftDefId;
        var slotB = current[1].ShiftDefId;
        var teamAtA = current[0].TeamId;
        var teamAtB = current[1].TeamId;

        var now = _clock.GetUtcNow().UtcDateTime;
        var swapped = new[]
        {
            new ShiftSlotAssignment(slotA, teamAtB, now, operatorName),
            new ShiftSlotAssignment(slotB, teamAtA, now, operatorName),
        };

        await _teamRepo.UpsertAssignmentsAtomicAsync(swapped, cancellationToken);

        // 内存快照刷新
        await RefreshAsync(cancellationToken);

        // 审计 — Detail 里把 before/after 都写清,便于排查"什么时候谁倒的班"
        var detail =
            $"swap: slot={slotA} team {before[slotA]}→{teamAtB} · slot={slotB} team {before[slotB]}→{teamAtA}";
        await _audit.WriteAsync(
            category: "ShiftRotation",
            detail: detail,
            user: operatorName,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "倒班对调完成 · 操作员 {Operator} · {Detail}", operatorName, detail);
    }

    /// <inheritdoc/>
    public async Task ReassignAsync(
        int shiftDefId, int teamId, string operatorName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        // 业务校验:teamId 必须是当前激活班组
        Team? team;
        lock (_gate)
        {
            team = _teams.FirstOrDefault(t => t.Id == teamId);
        }
        if (team is null)
        {
            throw new InvalidOperationException(
                $"班组 Id={teamId} 不存在或已停用,无法分配到槽位 {shiftDefId}");
        }

        // 拿到现有分配作为审计上下文
        var before = await _teamRepo.GetAssignmentAsync(shiftDefId, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        await _teamRepo.UpsertAssignmentAsync(
            new ShiftSlotAssignment(shiftDefId, teamId, now, operatorName),
            cancellationToken);

        await RefreshAsync(cancellationToken);

        var detail = before is null
            ? $"reassign: slot={shiftDefId} (new) → team {teamId}"
            : $"reassign: slot={shiftDefId} team {before.TeamId}→{teamId}";
        await _audit.WriteAsync(
            category: "ShiftRotation",
            detail: detail,
            user: operatorName,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "槽位重新分配完成 · 操作员 {Operator} · {Detail}", operatorName, detail);
    }

    private void RaiseChanged()
    {
        try { AssignmentsChanged?.Invoke(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AssignmentsChanged 订阅方抛出异常");
        }
    }
}
