namespace ProductionDashboard.Core.Shifts;

/// <summary>
/// 大屏「班次实时对比」只有两张卡片(顶栏也只有两个班组槽位):取排序(<see cref="ShiftDefinition.DisplayOrder"/>)
/// 最靠前的两个班次。默认的两班倒就是白班 + 夜班。
/// <para>
/// 三班及以上时,其余班次不占对比卡片,但照常计数:Hero 大数字和顶栏"当前班次"显示的是实际正在进行的班次。
/// 不按班次代号("A" / "B")查找,因为代号会随模板和删除而变化。
/// </para>
/// </summary>
public static class ComparedShifts
{
    /// <summary>取参与对比的两个班次;不足两个时对应位置为 null。</summary>
    public static (ShiftDefinition? First, ShiftDefinition? Second) Pick(IReadOnlyList<ShiftDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var ordered = definitions.OrderBy(d => d.DisplayOrder).ThenBy(d => d.Id).Take(2).ToArray();
        return (ordered.ElementAtOrDefault(0), ordered.ElementAtOrDefault(1));
    }
}
