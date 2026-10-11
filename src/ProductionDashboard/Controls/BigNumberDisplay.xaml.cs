using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionDashboard.Controls;

/// <summary>
/// 大数字 + 单位的复合显示。数字带白色到强调色的垂直渐变,
/// 等宽数字字形避免跳动。强调色 / 字号 / 单位都可通过依赖属性绑定。
/// </summary>
public partial class BigNumberDisplay : UserControl
{
    /// <summary>设计期 / XAML 加载用的默认构造。</summary>
    public BigNumberDisplay()
    {
        InitializeComponent();
    }

    // ── Value ──────────────────────────────────────────────────────

    /// <summary><see cref="Value"/> 依赖属性。</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(BigNumberDisplay),
            new PropertyMetadata("0"));

    /// <summary>显示的数字(格式化后字符串,例如 "1,247")。</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    // ── Unit ───────────────────────────────────────────────────────

    /// <summary><see cref="Unit"/> 依赖属性。</summary>
    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(
            nameof(Unit),
            typeof(string),
            typeof(BigNumberDisplay),
            new PropertyMetadata("件"));

    /// <summary>数字右侧的单位文字,例如 "件" / "件/h" / "%"。</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    // ── AccentColor (渐变终止色) ───────────────────────────────────

    /// <summary><see cref="AccentColor"/> 依赖属性。</summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(BigNumberDisplay),
            new PropertyMetadata(Color.FromArgb(0xFF, 0x00, 0xD4, 0xFF)));

    /// <summary>数字渐变的下半端颜色;上端固定走 Color.Text0(主文字色)。
    /// 不同卡片(A 班 / B 班 / 累计 / 目标 …)传不同的强调色让数字一眼可辨。</summary>
    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    // ── NumberFontSize ─────────────────────────────────────────────

    /// <summary><see cref="NumberFontSize"/> 依赖属性。</summary>
    public static readonly DependencyProperty NumberFontSizeProperty =
        DependencyProperty.Register(
            nameof(NumberFontSize),
            typeof(double),
            typeof(BigNumberDisplay),
            new PropertyMetadata(64.0));

    /// <summary>数字字号(DIP)。默认 64,Hero 区可上调,普通卡片调小。</summary>
    public double NumberFontSize
    {
        get => (double)GetValue(NumberFontSizeProperty);
        set => SetValue(NumberFontSizeProperty, value);
    }

    // ── UnitFontSize ───────────────────────────────────────────────

    /// <summary><see cref="UnitFontSize"/> 依赖属性。</summary>
    public static readonly DependencyProperty UnitFontSizeProperty =
        DependencyProperty.Register(
            nameof(UnitFontSize),
            typeof(double),
            typeof(BigNumberDisplay),
            new PropertyMetadata(18.0));

    /// <summary>单位字号(DIP)。一般跟数字字号成比例放小,默认 18。</summary>
    public double UnitFontSize
    {
        get => (double)GetValue(UnitFontSizeProperty);
        set => SetValue(UnitFontSizeProperty, value);
    }
}
