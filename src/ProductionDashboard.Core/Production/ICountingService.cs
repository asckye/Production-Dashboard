namespace ProductionDashboard.Core.Production;

/// <summary>
/// 当前班次的实时计数服务。提供毫秒级读取的内存计数器,
/// 以及计数变更通知,供 UI 大屏订阅刷新。
/// <para>
/// 数据归属按班组(Team)粒度:同一个班次周期内若发生倒班,
/// 字典里会出现多个 TeamId(各占一段产量),互不影响。
/// </para>
/// </summary>
public interface ICountingService
{
    /// <summary>取指定班组在当前班次周期的累计数。无该班组(或本周期无产量)返回 0。</summary>
    int GetCountForTeam(int teamId);

    /// <summary>
    /// 当前班次周期内所有班组累计数的只读快照。仅包含 >0 的班组。
    /// 倒班发生后旧班组的累计仍在(那是他们的产量,不能丢)。
    /// </summary>
    IReadOnlyDictionary<int, int> CurrentCountsByTeam { get; }

    /// <summary>事件:某班组的本班次累计数发生变化。参数 (TeamId, 新计数)。</summary>
    event Action<int, int>? TeamCountChanged;
}
