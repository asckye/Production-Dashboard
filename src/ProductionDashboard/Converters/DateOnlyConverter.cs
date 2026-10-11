using System.Globalization;
using System.Windows.Data;

namespace ProductionDashboard.Converters;

/// <summary>
/// WPF DatePicker 只识别 DateTime?,我们的 VM 用 DateOnly,
/// 这个 converter 让两者互通。
/// </summary>
public sealed class DateOnlyConverter : IValueConverter
{
    /// <summary>DateOnly → DateTime? (取 0:00),用于 DatePicker.SelectedDate 绑定。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateOnly d => (DateTime?)d.ToDateTime(TimeOnly.MinValue),
            _ => null,
        };

    /// <summary>DateTime → DateOnly。null 时回退到今天,保证 VM 字段非空。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.FromDateTime(DateTime.Today),
        };
}
