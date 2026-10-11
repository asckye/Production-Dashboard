using Microsoft.Extensions.Logging;
using ProductionDashboard.Core.Diagnostics;

namespace ProductionDashboard.Core.Shifts;

/// <summary><see cref="IShiftConfigurationService"/> 的实现。</summary>
public sealed class ShiftConfigurationService : IShiftConfigurationService
{
    private readonly IShiftDefinitionRepository _shiftRepository;
    private readonly ITeamRepository _teamRepository;
    private readonly IShiftManager _shifts;
    private readonly ITeamAssignmentService _teams;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShiftConfigurationService> _logger;

    /// <summary>注入班次 / 班组仓库、需要刷新的运行时服务、时钟和日志。</summary>
    public ShiftConfigurationService(
        IShiftDefinitionRepository shiftRepository,
        ITeamRepository teamRepository,
        IShiftManager shifts,
        ITeamAssignmentService teams,
        IAuditLogger audit,
        TimeProvider clock,
        ILogger<ShiftConfigurationService> logger)
    {
        _shiftRepository = shiftRepository;
        _teamRepository = teamRepository;
        _shifts = shifts;
        _teams = teams;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SaveShiftDefinitionsAsync(ShiftDefinitionChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        // 删除立即生效:最后生效日设为昨天,今天起的班次列表里就不再有它。
        // (设为今天的话,当天重新加载时它仍算"生效",删掉的班次会在界面上重新出现。)
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var deletedLastEffectiveDay = today.AddDays(-1);

        await _shiftRepository.SaveChangesAsync(
            changes, deletedLastEffectiveDay, _clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);

        foreach (var id in changes.DeletedIds)
        {
            _logger.LogInformation("班次软删除 · Id={Id} · 自 {Date} 起不再生效", id, today);
        }
        foreach (var definition in changes.Added)
        {
            _logger.LogInformation("班次新增 · {Code} {Name}", definition.Code, definition.Name);
        }
        foreach (var definition in changes.Updated)
        {
            _logger.LogInformation(
                "班次已更新 · {Code} {Name} {Start}-{End} 目标={Target}",
                definition.Code, definition.Name, definition.StartTime, definition.EndTime, definition.TargetCount);
        }

        var teamByCode = changes.TeamByCode ?? new Dictionary<string, int>();
        foreach (var (code, teamId) in teamByCode)
        {
            _logger.LogInformation("班次班组分配 · {Code} → 班组 Id={TeamId}", code, teamId);
        }

        await _shifts.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await _teams.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (changes.DeletedIds.Count > 0 || changes.Added.Count > 0 || changes.Updated.Count > 0 || teamByCode.Count > 0)
        {
            var detail =
                $"shifts: deleted=[{string.Join(",", changes.DeletedIds)}] "
                + $"added=[{string.Join(",", changes.Added.Select(d => d.Code))}] "
                + $"updated=[{string.Join(",", changes.Updated.Select(d => d.Code))}] "
                + $"teams=[{string.Join(",", teamByCode.Select(kv => $"{kv.Key}→{kv.Value}"))}]";
            await _audit.WriteAsync("ShiftConfig", detail, changes.ChangedBy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task SaveTeamsAsync(IReadOnlyList<Team> teams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(teams);

        foreach (var team in teams)
        {
            await _teamRepository.UpdateTeamAsync(team, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("班组已更新 · Id={Id} {Name} (班长 {Leader})", team.Id, team.Name, team.LeaderName);
        }

        await _teams.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
