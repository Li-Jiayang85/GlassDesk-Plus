using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;

/// <summary>
/// Windows 10 降级路径验收。
/// </summary>
/// <remarks>
/// 手头没有 Windows 10 机器时，用能力探测的构建号覆盖把程序“当成”跑在 19045 上，
/// 验证两件最容易在旧系统上出事的事：
///
/// <list type="number">
/// <item>窗口还能不能正常创建、显示——而不是在某个 Win11 专属调用上抛异常；</item>
/// <item>程序还会不会去请求系统背景材质与 DWM 圆角——这两样在 Win10 上不存在，
/// 请求了要么被拒、要么行为未定义，正确做法是压根不问。</item>
/// </list>
///
/// 这不能替代真机验证（真实 Win10 的 DWM 行为仍需实机确认），
/// 但能保证“代码路径在降级能力集下不会崩、也不会越权请求”。
/// </remarks>
internal static partial class Program
{
    private static int VerifyWin10Fallback(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "win10-fallback.json"));
        var supplied = Argument(args, "--data-dir");
        var root = supplied is null
            ? Path.Combine(AppContext.BaseDirectory, "win10-data-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(supplied);
        var real = AppPaths.ForCurrentUser().RootDirectory;
        if (root.Equals(real, StringComparison.OrdinalIgnoreCase) || root.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to use the user's GlassDesk data directory.");
            return 2;
        }
        Directory.CreateDirectory(root);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml", UriKind.Absolute),
        });

        DockWindow? dock = null;
        var realBuild = WindowsCapabilities.BuildNumber;
        try
        {
            // Windows 10 22H2 的构建号。
            WindowsCapabilities.OverrideBuildForVerification(19045);

            Check("Win10：19045 落在支持范围内", "W10-01",
                WindowsCapabilities.IsSupported && WindowsCapabilities.IsWindows10 && !WindowsCapabilities.IsWindows11OrGreater,
                "IsSupported=true、IsWindows10=true、IsWindows11OrGreater=false",
                $"IsSupported={WindowsCapabilities.IsSupported}、IsWindows10={WindowsCapabilities.IsWindows10}、IsWindows11OrGreater={WindowsCapabilities.IsWindows11OrGreater}");
            Check("Win10：不请求系统背景材质", "W10-02",
                !WindowsCapabilities.SupportsSystemBackdrop,
                "SupportsSystemBackdrop=false", WindowsCapabilities.SupportsSystemBackdrop.ToString());
            Check("Win10：不请求 DWM 圆角偏好", "W10-03",
                !WindowsCapabilities.SupportsWindowCornerPreference,
                "SupportsWindowCornerPreference=false", WindowsCapabilities.SupportsWindowCornerPreference.ToString());
            Check("Win10：19041 以下判为不支持", "W10-04",
                !WindowsCapabilities.IsProductBuildSupported(19040) && WindowsCapabilities.IsProductBuildSupported(19041),
                "19040=false、19041=true",
                $"19040={WindowsCapabilities.IsProductBuildSupported(19040)}、19041={WindowsCapabilities.IsProductBuildSupported(19041)}");

            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            using var services = new AppServiceHost(data);
            dock = new DockWindow();
            dock.AttachServices(services, DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher), () => { });
            dock.Show();
            Pump(TimeSpan.FromMilliseconds(300));

            Check("Win10：Dock 窗口能创建并显示", "W10-05",
                dock.IsLoaded && dock.IsVisible,
                "窗口已加载且可见", $"IsLoaded={dock.IsLoaded}、IsVisible={dock.IsVisible}");
            Check("Win10：窗口不走亚克力（未在 Win10 白名单）", "W10-06",
                dock.SurfaceMode != GlassSurfaceMode.Acrylic,
                "SurfaceMode != Acrylic", dock.SurfaceMode.ToString());

            // 直接向 DWM 递交 Win11 才有的外观请求：在降级能力下必须安静跳过，
            // 既不抛异常也不声称已经生效。
            var handle = new WindowInteropHelper(dock).Handle;
            var appearance = WindowAppearance.Apply(handle, new WindowAppearanceRequest
            {
                Backdrop = SystemBackdropType.MainWindow,
                Corners = WindowCornerPreference.Round,
                DarkMode = true,
                ExtendFrameIntoClientArea = true,
            });
            Check("Win10：DWM 材质与圆角请求被安全跳过", "W10-07",
                !appearance.BackdropApplied && !appearance.CornersApplied,
                "BackdropApplied=false、CornersApplied=false",
                $"BackdropApplied={appearance.BackdropApplied}、CornersApplied={appearance.CornersApplied}");
            Check("Win10：深色标题栏仍可请求（19041 已支持）", "W10-08",
                appearance.DarkModeApplied,
                "DarkModeApplied=true", appearance.DarkModeApplied.ToString());
        }
        catch (Exception ex)
        {
            _failed++;
            Cases.Add(new
            {
                name = "win10-runner", ids = "W10-01..W10-08", driver = "WPF window under forced build 19045",
                expected = "no exception", actual = ex.ToString(), status = "Failed",
            });
        }
        finally
        {
            // 必须恢复真实构建号，否则同进程里后续检查会带着假的版本跑。
            WindowsCapabilities.OverrideBuildForVerification(0);
            dock?.Close();
            app.Shutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                simulatedBuild = 19045,
                realBuild,
                productFileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(DockWindow).Assembly.Location).FileVersion,
                passed = Cases.Count - _failed,
                failed = _failed,
                cases = Cases,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        Console.WriteLine($"Windows 10 降级路径：{Cases.Count - _failed} 通过，{_failed} 失败。报告：{report}");
        return _failed == 0 ? 0 : 1;
    }
}
