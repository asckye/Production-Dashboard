
namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 班次状态服务。负责:
/// <list type="bullet">
///   <item>从仓储加载当前生效的班次定义</item>
///   <item>提供当前时刻所属班次 (<see cref="Current"/>)</item>
///   <item>检测班次切换并触发 <see cref="ShiftChanged"/></item>
/// </list>
/// </summary>
public interface IShiftManager
{
    /// <summary>当前所属班次,启动期未完成 Refresh 之前可能为 null。</summary>
    ShiftAssignment? Current { get; }

    /// <summary>已加载的班次定义快照(只读,用于 UI 展示班次列表)。</summary>
    IReadOnlyList<ShiftDefinition> Definitions { get; }

    /// <summary>
    /// 旧班次 → 新班次。两端任意一个可以是 null
    /// (启动时旧 null;切到空档期时新 null)。
    /// 仅在班次"主体身份"切换时触发(ShiftDefId / ShiftDate 任一改变)。
    /// </summary>
    event Action<ShiftAssignment?, ShiftAssignment?>? ShiftChanged;

    /// <summary>
    /// 班次定义集合发生变化(任何字段:目标 / 名称 / 颜色 / 起止时间 / 新增 / 删除)。
    /// 配合 <see cref="RefreshAsync"/> 使用 — 用户在班次配置页 Save 后,
    /// UI(例如 Dashboard 目标值)需要立即感知。
    /// 不传任何参数,订阅方自己重读 <see cref="Definitions"/> / <see cref="Current"/>。
    /// </summary>
    event Action? DefinitionsChanged;

    /// <summary>查询某一本地时刻属于哪个班次(不影响 <see cref="Current"/>)。</summary>
    ShiftAssignment? GetShiftAt(DateTime localTime);

    /// <summary>从数据库重新加载班次定义并立即重算当前班次。</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}
