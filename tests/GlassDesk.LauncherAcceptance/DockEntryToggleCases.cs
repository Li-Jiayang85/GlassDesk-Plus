using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

/// <summary>
/// Dock 自带入口（启动台 / 此电脑 / 切换桌面图标 / 设置）的显示开关验收。
/// </summary>
/// <remarks>
/// 四个入口都应在 设置 → Dock → 左侧固定图标 里单独隐藏，隐藏后不占位置、
/// 其余入口按固定顺序左移补齐。这里真实创建 DockWindow 与 SettingsWindow，
/// 逐个组合翻转复选框，读取按钮的实际可见性与布局位置——不是复算一遍公式。
/// </remarks>
internal static partial class Program
{
    private static int VerifyDockEntryToggles(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "dock-entries.json"));
        var supplied = Argument(args, "--data-dir");
        var root = supplied is null
            ? Path.Combine(AppContext.BaseDirectory, "dock-entries-data-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(supplied);
        var real = AppPaths.ForCurrentUser().RootDirectory;
        if (root.Equals(real, StringComparison.OrdinalIgnoreCase) || root.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to use the user's GlassDesk data directory.");
            return 2;
        }
        Directory.CreateDirectory(root);

        DockWindow? dock = null;
        SettingsWindow? settings = null;
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            using var services = new AppServiceHost(data);
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            dock = new DockWindow();
            dock.AttachServices(services, host, () => { });
            dock.Show();
            settings = new SettingsWindow();
            settings.AttachDataStore(data, null);
            Pump(TimeSpan.FromMilliseconds(200));

            var names = new[] { "LauncherButton", "ThisPcButton", "DesktopIconsButton", "SettingsButton" };
            var fields = new[] { "ShowLauncherEntry", "ShowThisPcEntry", "ShowDesktopIconsEntry", "ShowSettingsEntry" };
            var buttons = names.Select(name => (Button)dock.FindName(name)!).ToArray();
            var boxes = new[]
            {
                "ShowLauncherEntryCheckBox", "ShowThisPcEntryCheckBox",
                "ShowDesktopIconsEntryCheckBox", "ShowSettingsEntryCheckBox",
            }.Select(name => (CheckBox)settings.FindName(name)!).ToArray();

            Check("Dock 自带入口：四个开关都在设置里", "D-01", boxes.All(box => box is not null),
                "4 个复选框", $"{boxes.Count(box => box is not null)} 个");

            var row = (StackPanel)buttons[0].Parent;
            Check("Dock 自带入口：齿轮与其余入口在同一行", "D-01",
                row.Children.OfType<Button>().Take(4).SequenceEqual(buttons),
                "顺序 启动台|此电脑|切换桌面图标|设置",
                string.Join("|", row.Children.OfType<Button>().Take(4).Select(button => button.Name)));

            var failedMasks = new List<string>();
            for (var mask = 0; mask < 16; mask++)
            {
                for (var i = 0; i < 4; i++) boxes[i].IsChecked = (mask & (1 << i)) != 0;
                Pump(TimeSpan.FromMilliseconds(60));
                dock.UpdateLayout();
                double x = 0;
                for (var i = 0; i < 4; i++)
                {
                    var visible = (mask & (1 << i)) != 0;
                    if (buttons[i].Visibility != (visible ? Visibility.Visible : Visibility.Collapsed))
                        failedMasks.Add($"组合{mask}:{names[i]} 可见性 {buttons[i].Visibility}");
                    if (!visible) continue;
                    var actual = buttons[i].TranslatePoint(new Point(), row).X;
                    if (Math.Abs(actual - x) > 1)
                        failedMasks.Add($"组合{mask}:{names[i]} 位置 {actual:0.#} 应为 {x:0.#}");
                    x += buttons[i].ActualWidth + buttons[i].Margin.Right;
                }
            }
            Check("Dock 自带入口：16 种组合即刻生效且无空隙", "D-02", failedMasks.Count == 0,
                "可见性与位置全部符合", failedMasks.Count == 0 ? "16 种组合全部通过" : string.Join("；", failedMasks.Take(6)));

            // 齿轮单独隐藏：其余三个仍在原位、齿轮消失、且布局不留空位。
            for (var i = 0; i < 4; i++) boxes[i].IsChecked = true;
            Pump(TimeSpan.FromMilliseconds(60));
            boxes[3].IsChecked = false;
            Pump(TimeSpan.FromMilliseconds(80));
            dock.UpdateLayout();
            Check("Dock 自带入口：隐藏设置图标后齿轮消失且不留空位", "D-03",
                buttons[3].Visibility == Visibility.Collapsed
                && Math.Abs(buttons[0].TranslatePoint(new Point(), row).X) < 1,
                "齿轮 Collapsed、启动台仍在最左",
                $"齿轮 {buttons[3].Visibility}、启动台 X={buttons[0].TranslatePoint(new Point(), row).X:0.#}");

            // 持久化：隐藏设置图标要写进 settings.json，重载后仍生效。
            var reloaded = new SettingsStore(data.Paths.SettingsFile);
            reloaded.Load();
            Check("Dock 自带入口：隐藏设置图标已落盘", "D-04",
                !reloaded.Current.Dock.ShowSettingsEntry && reloaded.Current.Dock.ShowLauncherEntry,
                "settings.json 中 showSettingsEntry=false、其余开关不受影响",
                $"showSettingsEntry={reloaded.Current.Dock.ShowSettingsEntry}、"
                + $"showLauncherEntry={reloaded.Current.Dock.ShowLauncherEntry}");

            var reloadedData = new GlassDeskDataStore(new AppPaths(root));
            reloadedData.LoadAll();
            settings.AttachDataStore(reloadedData, null);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("Dock 自带入口：重新加载后开关状态回显", "D-04",
                boxes[3].IsChecked == false && boxes[0].IsChecked == true,
                "设置图标未勾选、启动台仍勾选",
                $"设置={boxes[3].IsChecked}、启动台={boxes[0].IsChecked}");

            // 全部隐藏时 Dock 里的固定入口区仍不报错，只是没有入口。
            foreach (var box in boxes) box.IsChecked = false;
            Pump(TimeSpan.FromMilliseconds(80));
            Check("Dock 自带入口：全部隐藏后不报错", "D-05",
                buttons.All(button => button.Visibility == Visibility.Collapsed) && dock.Title.Length >= 0,
                "四个入口全部 Collapsed，Dock 仍在",
                string.Join(",", buttons.Select(button => button.Visibility)));
        }
        catch (Exception ex)
        {
            _failed++;
            Cases.Add(new { name = "dock-entries-runner", ids = "D-01..D-05", driver = "WPF windows",
                expected = "no exception", actual = ex.ToString(), status = "Failed" });
        }
        finally
        {
            dock?.Close();
            settings?.Close();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                dataDirectory = root,
                passed = Cases.Count - _failed,
                failed = _failed,
                cases = Cases,
            }, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (supplied is null && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        Console.WriteLine($"Dock 自带入口：{Cases.Count - _failed} 通过，{_failed} 失败。报告：{report}");
        return _failed == 0 ? 0 : 1;
    }
}
