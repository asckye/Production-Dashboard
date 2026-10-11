using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using ProductionDashboard.Infrastructure.Persistence;

namespace ProductionDashboard.Services.UI;

/// <inheritdoc cref="IThemeService"/>
public sealed class ThemeService : IThemeService
{
    private const string DarkUri = "/Resources/Themes/Dark.xaml";
    private const string LightUri = "/Resources/Themes/Light.xaml";

    private readonly ILogger<ThemeService> _logger;
    private readonly string _persistPath;
    private AppTheme _current;

    /// <summary>注入连接工厂(用于定位偏好 json)+ 日志。启动期恢复用户上次选择的主题。</summary>
    public ThemeService(
        IDbConnectionFactory dbFactory,
        ILogger<ThemeService> logger)
    {
        _logger = logger;
        // 主题偏好放数据库同目录的 theme.json,跟 Data/ 一起备份 / 跟随工控机
        var dbDir = Path.GetDirectoryName(dbFactory.DatabasePath) ?? AppContext.BaseDirectory;
        _persistPath = Path.Combine(dbDir, "theme.json");

        // 启动期从磁盘恢复;读不到默认深色
        _current = LoadPersistedOrDefault();
        // App.xaml 默认合并的是 Dark.xaml,如果恢复出来是 Light 需要在第一时间替换
        if (_current == AppTheme.Light)
        {
            try { ReplaceThemeDictionary(LightUri); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动期切换到浅色主题失败,继续用深色");
                _current = AppTheme.Dark;
            }
        }
    }

    /// <inheritdoc/>
    public AppTheme Current => _current;

    /// <inheritdoc/>
    public event Action<AppTheme>? ThemeChanged;

    /// <inheritdoc/>
    public void Apply(AppTheme theme)
    {
        if (_current == theme) return;

        var uri = theme == AppTheme.Light ? LightUri : DarkUri;
        ReplaceThemeDictionary(uri);
        _current = theme;
        Persist(theme);

        _logger.LogInformation("主题切换 → {Theme}", theme);

        try { ThemeChanged?.Invoke(theme); }
        catch (Exception ex) { _logger.LogWarning(ex, "ThemeChanged 订阅方抛异常"); }
    }

    /// <inheritdoc/>
    public void Toggle() => Apply(_current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    // ── 内部 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 替换 Application 资源里"主题字典"那条。识别规则:source 包含 /Resources/Themes/。
    /// </summary>
    private static void ReplaceThemeDictionary(string newUri)
    {
        var dicts = Application.Current?.Resources?.MergedDictionaries
            ?? throw new InvalidOperationException("Application.Current.Resources 尚未就绪");

        var newDict = new ResourceDictionary
        {
            Source = new Uri(newUri, UriKind.Relative),
        };

        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("/Resources/Themes/", StringComparison.OrdinalIgnoreCase))
            {
                dicts[i] = newDict;
                return;
            }
        }

        // 没找到旧的主题字典 → 直接插到最前面,保证后续 dict 能引用
        dicts.Insert(0, newDict);
    }

    private sealed record ThemePref(string Theme);

    private AppTheme LoadPersistedOrDefault()
    {
        try
        {
            if (!File.Exists(_persistPath)) return AppTheme.Dark;
            var json = File.ReadAllText(_persistPath);
            var pref = JsonSerializer.Deserialize<ThemePref>(json);
            if (pref is null) return AppTheme.Dark;
            return Enum.TryParse<AppTheme>(pref.Theme, ignoreCase: true, out var t) ? t : AppTheme.Dark;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取主题偏好失败,降级为深色 · {Path}", _persistPath);
            return AppTheme.Dark;
        }
    }

    private void Persist(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            var json = JsonSerializer.Serialize(
                new ThemePref(theme.ToString()),
                new JsonSerializerOptions { WriteIndented = true });
            // 原子写:先 tmp 再 move
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存主题偏好失败 · {Path}", _persistPath);
        }
    }
}
