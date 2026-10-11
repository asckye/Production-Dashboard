using Microsoft.Extensions.Logging;

namespace ProductionDashboard.Core.Shifts;

/// <summary><see cref="IShiftConfigurationService"/> 的实现。</summary>
public sealed class ShiftConfigurationService : IShiftConfigurationService
{
    private readonly IShiftDefinitionRepository _shiftRepository;
    private readonly ITeamRepository _teamRepository;
    private readonly IShiftManager _shifts;
    private readonly ITeamAssignmentService _teams;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShiftConfigurationService> _logger;

    /// <summary>注入班次 / 班组仓库、需要刷新的运行时服务、时钟和日志。</summary>
    public ShiftConfigurationService(
        IShiftDefinitionRepository shiftRepository,
        ITeamRepository teamRepository,
        IShiftManager shifts,
        ITeamAssignmentService teams,
        TimeProvider clock,
        ILogger<ShiftConfigurationService> logger)
    {
        _shiftRepository = shiftRepository;
        _teamRepository = teamRepository;
        _shifts = shifts;
        _teams = teams;
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

        await _shiftRepository.SaveChangesAsync(changes, deletedLastEffectiveDay, cancellationToken).ConfigureAwait(false);

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

        await _shifts.RefreshAsync(cancellationToken).ConfigureAwait(false);
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
