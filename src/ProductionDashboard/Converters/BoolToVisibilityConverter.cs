using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProductionDashboard.Converters;

/// <summary>
/// 标准 bool → Visibility 转换。WPF 自带的版本是 BooleanToVisibilityConverter 但不在
/// 全局资源里,这里包一下方便 ResourceDictionary 引用。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>bool → Visibility。ConverterParameter="Inverse" 时反转语义。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        // ConverterParameter="Inverse" 反转语义
        if (parameter is string p && string.Equals(p, "Inverse", StringComparison.OrdinalIgnoreCase))
        {
            b = !b;
        }
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Visibility → bool 反向(同样支持 Inverse 反转)。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value is Visibility.Visible;
        if (parameter is string p && string.Equals(p, "Inverse", StringComparison.OrdinalIgnoreCase))
        {
            v = !v;
        }
        return v;
    }
}
