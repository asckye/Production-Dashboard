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

        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        foreach (var id in changes.DeletedIds)
        {
            await _shiftRepository.SoftDeleteShiftDefinitionAsync(id, today, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("班次软删除 · Id={Id} · 自 {Date} 起不再生效", id, today);
        }

        foreach (var definition in changes.Added)
        {
            var newId = await _shiftRepository.InsertShiftDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("班次新增 · {Code} {Name} → Id={Id}", definition.Code, definition.Name, newId);
        }

        foreach (var definition in changes.Updated)
        {
            await _shiftRepository.UpdateShiftDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
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
