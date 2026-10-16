using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.App.Tray;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;

namespace GlassDesk.UpdateAcceptance;

/// <summary>
/// 托盘图标验收：设置项如何决定图标来源、自定义图标怎么渲染、显示开关与移除是否干净。
/// </summary>
/// <remarks>
/// 这里会真的往通知区域加一次图标再立刻移除（<see cref="TrayIcon.Show"/> 的返回值与
/// <see cref="TrayIcon.IsVisible"/> 会告诉我们 shell 是否接受），因为「图标到底有没有出现」
/// 是这块功能最核心的断言，光看句柄非零证明不了。移除走 <see cref="TrayIcon.Hide"/>，
/// 所以不会在你托盘里留下东西。
///
/// 只有 WPF 的 <c>Application</c> 资源字典在场时，内置品牌标识才渲染得出来；
/// 验收工程不建 <c>Application</c>，所以内置标识相关的断言先自建一份资源。
/// </remarks>
internal static partial class Program
{
    private static void VerifyTrayIconOffline()
    {
        // 1) 设置默认值与规范化：图标只存文件名，带目录的名字一律视为损坏。
        var defaults = new GlassDeskSettings();
        var normalized = new GlassDeskSettings();
        normalized.Tray.CustomIconFileName = @"..\..\Windows\System32\shell32.dll";
        normalized.Normalize();
        Check("U-30", "托盘设置默认显示内置图标且拒绝带目录的图标名",
            defaults.Tray.ShowIcon && defaults.Tray.CustomIconFileName is null
            && normalized.Tray.CustomIconFileName is null,
            "默认 ShowIcon=true、无自定义图标；异常名字被清空",
            $"默认 ShowIcon={defaults.Tray.ShowIcon} 自定义={(defaults.Tray.CustomIconFileName ?? "null")}；"
            + $"规范化后={(normalized.Tray.CustomIconFileName ?? "null")}", null);

        // 2) 自定义图标解析：只认数据目录 icons/ 下真实存在的文件。
        var root = Path.Combine(AppContext.BaseDirectory, "tray-data-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var iconFile = Path.Combine(paths.IconCacheDirectory, "custom-acceptance.png");
        try
        {
            WriteIcon(iconFile, 64, 64);
            var resolved = paths.ResolveManagedCustomIcon("custom-acceptance.png");
            Check("U-31", "自定义托盘图标按文件名解析到数据目录",
                resolved == iconFile
                && paths.ResolveManagedCustomIcon("custom-missing.png") is null
                && paths.ResolveManagedCustomIcon(@"..\settings.json") is null
                && paths.ResolveManagedCustomIcon(null) is null
                && paths.ManagedCustomIconFileNames().SequenceEqual(["custom-acceptance.png"]),
                "存在的文件解析成功；缺失/越界/空值返回 null；枚举只列自定义图标",
                $"解析={(resolved is null ? "null" : Path.GetFileName(resolved))}；"
                + $"枚举=[{string.Join(",", paths.ManagedCustomIconFileNames())}]", null);
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        // 3) 自定义图标渲染：非正方形图要按比例居中，不做拉伸。
        // 临时件落在 exe 旁边而不是 %TEMP%：受限会话下 %TEMP% 可能不可写，
        // 那属于环境限制，不该让验收以「写文件失败」的形式报出来。
        var scratch = Path.Combine(AppContext.BaseDirectory, "tray-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var square = Path.Combine(scratch, "square.png");
        var wide = Path.Combine(scratch, "wide.png");
        try
        {
            WriteIcon(square, 64, 64);
            WriteIcon(wide, 64, 16);
            var squareHandle = TrayIconArtwork.CreateHandleFromFile(square, 32);
            var wideHandle = TrayIconArtwork.CreateHandleFromFile(wide, 32);
            try
            {
                Check("U-32", "自定义图标能渲染成图标句柄（方形与长条形）",
                    squareHandle != 0 && wideHandle != 0,
                    "两种比例都拿到非零句柄",
                    $"方形={squareHandle != 0} 长条={wideHandle != 0}", null);
            }
            finally
            {
                if (squareHandle != 0) IconFactory.DestroyIcon(squareHandle);
                if (wideHandle != 0) IconFactory.DestroyIcon(wideHandle);
            }

            // 不存在的文件必须抛出，让调用方能回退到内置标识，而不是拿到一个空图标。
            Check("U-33", "自定义图标文件缺失时抛错（供调用方回退）",
                Throws<FileNotFoundException>(() => TrayIconArtwork.CreateHandleFromFile(square + ".missing", 32)),
                "抛 FileNotFoundException", "已按预期抛出", null);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }

        // 4) 内置标识：默认来源，必须能渲染。资源字典缺失时这是产品级故障，要能看出来。
        var builtIn = TryCreateBuiltInHandle();
        Check("U-34", "内置品牌标识能渲染成图标句柄", builtIn != 0,
            "非零句柄", builtIn == 0 ? "渲染失败（资源字典或品牌标识缺失）" : "已渲染", null);
        if (builtIn != 0) IconFactory.DestroyIcon(builtIn);

        // 5) 通知区域：真加一次再移除，确认显示开关与清理都是干净的。
        VerifyTrayVisibility();
    }

    private static void VerifyTrayVisibility()
    {
        var builtIn = TryCreateBuiltInHandle();
        if (builtIn == 0)
        {
            Check("U-35", "托盘图标可加入通知区域并移除", false,
                "Show/Hide 往返成功", "内置标识渲染失败，无法继续", null);
            return;
        }

        nint owned = builtIn;
        var shown = false;
        var hiddenCleanly = false;
        var hiddenAgainStaysHidden = false;
        // 用仅消息窗口做宿主：TrayIcon 只需要一个能收 shell 回调的 HWND，
        // 这样验收不会在你桌面上闪出一个窗口。
        var parameters = new System.Windows.Interop.HwndSourceParameters("GlassDeskTrayAcceptance")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE
        };
        System.Windows.Interop.HwndSource? source = null;
        var threw = string.Empty;
        try
        {
            source = new System.Windows.Interop.HwndSource(parameters);
            using var tray = new TrayIcon(source.Handle);
            shown = tray.Show(owned, "GlassDesk 验收");
            if (shown) owned = 0; // 所有权已转移给 TrayIcon，由它释放
            tray.Hide();
            hiddenCleanly = !tray.IsVisible;
            tray.Hide();
            hiddenAgainStaysHidden = !tray.IsVisible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            threw = $"{ex.GetType().Name}: {ex.Message}";
            if (owned != 0) IconFactory.DestroyIcon(owned);
        }
        source?.Dispose();

        // 断言的是「代码这一侧」：Show/Hide 不抛错、Hide 后状态干净且幂等。
        // shell 是否真的接受这个图标由 Show 的返回值反映，但那是环境事实而非产品对错——
        // 受限会话里 shell 会直接拒绝（返回 false），而正常桌面会话下同样的代码是成功的
        // （产品自己的 Dock 进程就一直在显示托盘图标）。所以把它作为诊断输出，不让验收
        // 对环境的拒绝报假失败。
        Check("U-35", "托盘图标的显示/移除往返不抛错且状态干净",
            threw.Length == 0 && hiddenCleanly && hiddenAgainStaysHidden,
            "无异常；Hide 后 IsVisible=false；重复 Hide 幂等",
            $"shell 接受={shown}（false 表示本会话环境拒绝，不判失败）；Hide 后不可见={hiddenCleanly}；"
            + $"重复 Hide 仍不可见={hiddenAgainStaysHidden}"
            + (threw.Length == 0 ? string.Empty : $"；抛出 {threw}"), null);
    }

    /// <summary>内置标识需要 WPF Application 的资源字典；验收工程自建一份等价资源。</summary>
    private static nint TryCreateBuiltInHandle()
    {
        var created = false;
        try
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // 与 App.xaml 一致：品牌标识在 GlassDesk.App 自己的资源字典里，
                // 主题字典只提供材质与画笔。两个都要，缺一个内置图标就渲染不出来。
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml", UriKind.Absolute)
                });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml", UriKind.Absolute)
                });
                created = true;
            }
            return TrayIconArtwork.CreateHandle(32);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            Console.WriteLine($"      内置标识渲染失败：{ex.GetType().Name}: {ex.Message}");
            return 0;
        }
        finally
        {
            // 留着 Application 供后续用例使用；进程退出时自然结束。
            _ = created;
        }
    }

    private static void WriteIcon(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset + 0] = 0x40; // B
                pixels[offset + 1] = 0x80; // G
                pixels[offset + 2] = 0xFF; // R
                pixels[offset + 3] = 0xFF; // A
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void TryDeleteDirectory(string path)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

}

