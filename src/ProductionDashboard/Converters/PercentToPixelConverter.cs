using System.Globalization;
using System.Windows.Data;

namespace ProductionDashboard.Converters;

/// <summary>
/// 把 (percent 0..100, containerWidth) 映射成像素值。
/// 24h 时间轴在 ShiftConfigView 中用来把 LeftPercent / WidthPercent 转成
/// Canvas.Left / Width。
/// </summary>
public sealed class PercentToPixelConverter : IMultiValueConverter
{
    /// <summary>values = [percent (0-100), containerWidth] → percent * width / 100。</summary>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return 0d;
        if (values[0] is not double pct) return 0d;
        if (values[1] is not double width) return 0d;
        if (double.IsNaN(width) || width <= 0) return 0d;
        return pct * width / 100.0;
    }

    /// <summary>单向 converter,不支持反向转换。</summary>
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
