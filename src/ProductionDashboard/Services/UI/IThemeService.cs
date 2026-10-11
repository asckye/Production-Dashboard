namespace ProductionDashboard.Services.UI;

/// <summary>UI 主题。</summary>
public enum AppTheme
{
    /// <summary>深色 — 工厂大屏默认。</summary>
    Dark = 0,

    /// <summary>浅色 — 办公室 / 演示场景。</summary>
    Light = 1,
}

/// <summary>
/// 主题切换服务。换主题 = 替换 <c>Application.Resources.MergedDictionaries</c> 里的
/// Themes/*.xaml 字典;所有 XAML 用 <c>DynamicResource</c> 引用的 Brush/Color 会自动重解析。
/// 切换结果会写到 <c>Data/theme.json</c>,下次启动自动恢复。
/// </summary>
public interface IThemeService
{
    /// <summary>当前生效的主题。</summary>
    AppTheme Current { get; }

    /// <summary>主题切换完成后触发(UI 可借此刷新派生属性,例如 ☀ / 🌙 图标)。</summary>
    event Action<AppTheme>? ThemeChanged;

    /// <summary>切到目标主题。若已经是该主题则只做幂等处理。失败抛异常。</summary>
    void Apply(AppTheme theme);

    /// <summary>在当前两个主题之间切换。</summary>
    void Toggle();
}
