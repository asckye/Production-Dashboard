using System.Runtime.CompilerServices;
using System.Windows;

// WPF 主题资源定位规则:
//   1) ResourceDictionaryLocation.None         — 主题相关字典的位置(本项目不使用主题切换,
//                                                  当资源在 Page / Application 字典中找不到时,
//                                                  不再去主题字典里查找)
//   2) ResourceDictionaryLocation.SourceAssembly — 通用(generic.xaml)字典的位置,
//                                                  当资源在 Page / App / 主题字典中都找不到时,
//                                                  会回退到本程序集内的 themes/generic.xaml
[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]

// 测试项目需要访问 internal 类型(例如 BenignTaskExceptionFilter)做单元测试。
// 只暴露给同方案的测试程序集,生产分发不会泄露 internal API。
[assembly: InternalsVisibleTo("ProductionDashboard.Tests")]
