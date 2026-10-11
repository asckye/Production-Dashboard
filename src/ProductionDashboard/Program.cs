using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ProductionDashboard.Watchdog;

namespace ProductionDashboard;

/// <summary>
/// 显式入口。负责把进程分流到两条独立路径:
/// <list type="bullet">
///   <item><c>--watchdog ...</c> → 守护进程模式,纯控制台,不创建 WPF</item>
///   <item>其它参数 → 启动 WPF Application</item>
/// </list>
/// 用显式 Main 是为了让守护进程不需要 Dispatcher/WPF 资源,
/// 同时主程序仍然有完整的 STA + Application.Run 生命周期。
/// </summary>
public static class Program
{
    // 全局命名 Mutex,保证一台机器同一用户只能起一个主程序实例。
    // Local\ 前缀让它只在当前会话生效 (远程桌面里多用户互不影响)。
    // GUID 是为了避开和系统其他进程的命名碰撞。
    private const string SingleInstanceMutexName =
        @"Local\ProductionDashboard.SingleInstance.{D7E2C4F8-8A1B-4C5E-9F3D-2B6E1A7C9D4F}";

    /// <summary>程序入口。STA 线程模式(WPF 必需)。返回值即进程 ExitCode。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // 守护进程模式不受单实例锁约束 — 它本来就是主程序 fork 出来的兄弟
        if (args.Length > 0 &&
            Array.Exists(args, a => string.Equals(a, "--watchdog", StringComparison.Ordinal)))
        {
            return WatchdogRunner.Run(args);
        }

        // 单实例锁 — Mutex 生命周期跟随进程,主动 Dispose 释放
        using var mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out _);
        var owned = false;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程崩溃没释放,但 Mutex 已被我们继承 — 视为获得锁
            owned = true;
        }

        if (!owned)
        {
            // 此时 WPF Application 还没建,IToastService 不可用。
            // 用一个临时 borderless Window 模拟 toast,4 秒后自动消失。
            ShowStandaloneToast(
                title: "重复启动",
                message: "ProductionDashboard 已在运行中。\n如果窗口看不到,请检查任务管理器。",
                lifetimeMs: 4000);
            return 1;
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* 进程退出阶段失败可忽略 */ }
        }
    }

    /// <summary>
    /// WPF Application 还没启动时的"独立 toast":借用 borderless Window 在右下角短暂显示,
    /// 到点自动 Close。仅用于单实例冲突等极少数早期路径,主流程一律走 IToastService。
    /// </summary>
    private static void ShowStandaloneToast(string title, string message, int lifetimeMs)
    {
        var workArea = SystemParameters.WorkArea;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 14, 20, 14),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.4 },
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)),
            Margin = new Thickness(0, 0, 0, 6),
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        });
        border.Child = stack;

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = border,
        };

        // 右下角对齐(扣掉 24px 边距)。SizeToContent 让宽高在 Loaded 后才确定,
        // 因此在 Loaded 事件里再设最终坐标。
        window.Loaded += (_, _) =>
        {
            window.Left = workArea.Right - window.ActualWidth - 24;
            window.Top = workArea.Bottom - window.ActualHeight - 24;
        };

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(lifetimeMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        // 如果用户用 Alt+F4 等外部方式关掉窗口,要主动 Stop timer,
        // 避免 ShowDialog 返回后还有一个孤儿 timer 在 Dispatcher 队列里(进程虽然很快退出,但更干净)。
        window.Closed += (_, _) => timer.Stop();
        timer.Start();

        // ShowDialog 阻塞当前线程直到 window.Close — 进程因此不会立刻退出,
        // 操作员能看到提示再走 return 1。
        window.ShowDialog();
    }
}
