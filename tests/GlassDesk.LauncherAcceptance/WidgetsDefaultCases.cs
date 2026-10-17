using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

/// <summary>
/// 小组件默认关闭的验收：新配置里总开关与每个小组件都应是关的，Dock 上不出现小组件图标；
/// 打开总开关并单独启用某个小组件后，只出现那一个。
/// </summary>
/// <remarks>
/// 「保持每个小组件的独立开关」是这条需求的后半句，所以这里不只看默认值，
/// 还要验证总开关打开后逐个启用确实只影响对应的小组件。
/// </remarks>
internal static partial class Program
{
    private static int VerifyWidgetsDefaultOff(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "widgets-default.json"));
        var supplied = Argument(args, "--data-dir");
        var root = supplied is null
            ? Path.Combine(AppContext.BaseDirectory, "widgets-data-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(supplied);
        var real = AppPaths.ForCurrentUser().RootDirectory;
        if (root.Equals(real, StringComparison.OrdinalIgnoreCase) || root.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to use the user's GlassDesk data directory.");
            return 2;
        }
        Directory.CreateDirectory(root);

        DockWindow? dock = null;
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var settings = data.Settings.Current;

            Check("小组件：新配置里八个小组件全部默认关闭", "W-01",
                settings.Widgets.Count == 8 && settings.Widgets.All(w => !w.Enabled),
                "8 个且 Enabled 全为 false",
                $"{settings.Widgets.Count} 个，开启 {settings.Widgets.Count(w => w.Enabled)} 个");
            Check("小组件：总开关不再起门禁作用（界面已移除）", "W-01",
                settings.WidgetsEnabled,
                "WidgetsEnabled=true（仅保留字段兼容）", settings.WidgetsEnabled.ToString());

            var host = WidgetSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(settings.WidgetsEnabled, settings.Widgets);

            using var services = new AppServiceHost(data);
            dock = new DockWindow();
            dock.AttachServices(services, DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher), () => { });
            dock.Show();
            Pump(TimeSpan.FromMilliseconds(250));

            Check("小组件：默认状态下 Dock 不显示任何小组件图标", "W-02",
                VisibleWidgetCount(dock) == 0,
                "0 个可见小组件",
                $"{VisibleWidgetCount(dock)} 个");

            // 单独启用时钟：只出现时钟一个（没有任何总开关参与）。
            var onlyClock = settings.Widgets.Select(w => new WidgetSettings
            {
                Kind = w.Kind, Enabled = w.Kind == WidgetKind.Clock, Size = w.Size,
                DockDocked = w.DockDocked, RefreshIntervalMs = w.RefreshIntervalMs,
            }).ToList();
            host.Update(settings.WidgetsEnabled, onlyClock);
            Pump(TimeSpan.FromMilliseconds(200));
            Check("小组件：独立开关只放出被选中的那一个", "W-03",
                VisibleWidgetCount(dock) == 1,
                "1 个可见小组件（时钟）",
                WidgetSummary(dock));

            // 再单独启用日期：两个都显示，互不影响。
            var clockAndDate = onlyClock.Select(w => new WidgetSettings
            {
                Kind = w.Kind, Enabled = w.Kind is WidgetKind.Clock or WidgetKind.Date, Size = w.Size,
                DockDocked = w.DockDocked, RefreshIntervalMs = w.RefreshIntervalMs,
            }).ToList();
            host.Update(settings.WidgetsEnabled, clockAndDate);
            Pump(TimeSpan.FromMilliseconds(200));
            Check("小组件：两个独立开关可同时生效", "W-04",
                VisibleWidgetCount(dock) == 2,
                "2 个可见小组件（时钟 + 日期）",
                WidgetSummary(dock));

            // 关掉时钟：只剩日期，证明是各自独立而不是全有全无。
            var onlyDate = clockAndDate.Select(w => new WidgetSettings
            {
                Kind = w.Kind, Enabled = w.Kind == WidgetKind.Date, Size = w.Size,
                DockDocked = w.DockDocked, RefreshIntervalMs = w.RefreshIntervalMs,
            }).ToList();
            host.Update(settings.WidgetsEnabled, onlyDate);
            Pump(TimeSpan.FromMilliseconds(200));
            Check("小组件：关掉其中一个不影响另一个", "W-05",
                VisibleWidgetCount(dock) == 1,
                "1 个可见小组件（日期）",
                WidgetSummary(dock));

            // 旧配置迁移：widgetsEnabled=false 的语义应落到「各小组件关闭」，且不再挡人。
            var legacy = new GlassDeskSettings();
            legacy.WidgetsEnabled = false;
            foreach (var widget in legacy.Widgets) widget.Enabled = true;
            legacy.Normalize();
            Check("小组件：旧配置 widgetsEnabled=false 迁移为各开关关闭", "W-06",
                legacy.WidgetsEnabled && legacy.Widgets.All(w => !w.Enabled),
                "迁移后字段为 true、八个小组件全关",
                $"字段={legacy.WidgetsEnabled}、开启 {legacy.Widgets.Count(w => w.Enabled)} 个");

            // 重置该页应回到「全部关闭」。
            var reset = SettingsPageEditor.ResetPage(settings, "小组件");
            Check("小组件：恢复默认后回到全部关闭", "W-07",
                reset.Widgets.All(w => !w.Enabled),
                "八个全关",
                $"开启 {reset.Widgets.Count(w => w.Enabled)} 个");

            // 刷新频率是固定值：时钟 1 秒、天气 30 分钟，其余 1 秒。
            var clockMs = settings.Widgets.Single(w => w.Kind == WidgetKind.Clock).RefreshIntervalMs;
            var weatherMs = settings.Widgets.Single(w => w.Kind == WidgetKind.Weather).RefreshIntervalMs;
            var legacyWeather = new GlassDeskSettings();
            legacyWeather.Normalize(); // 新实例的 Widgets 是空的，先补成八个
            legacyWeather.Widgets.Single(w => w.Kind == WidgetKind.Weather).RefreshIntervalMs = 5000;
            legacyWeather.Normalize();
            Check("小组件：刷新频率为固定值（时钟 1 秒 / 天气 30 分钟）", "W-08",
                clockMs == 1000 && weatherMs == 1_800_000
                && legacyWeather.Widgets.Single(w => w.Kind == WidgetKind.Weather).RefreshIntervalMs == 1_800_000,
                "时钟 1000ms、天气 1800000ms；旧配置的 5000ms 被纠正",
                $"时钟={clockMs}ms、天气={weatherMs}ms、旧配置纠正后={legacyWeather.Widgets.Single(w => w.Kind == WidgetKind.Weather).RefreshIntervalMs}ms");

            // 界面上不再有刷新频率下拉框。
            var settingsWindow = new SettingsWindow();
            try
            {
                settingsWindow.AttachDataStore(data, null);
                var removed = new[] { "WidgetClockRefreshCombo", "WidgetDateRefreshCombo", "WidgetMonitorRefreshCombo", "WidgetWeatherRefreshCombo" };
                var stillThere = removed.Where(name => settingsWindow.FindName(name) is not null).ToArray();
                Check("小组件：设置界面不再展示刷新频率选择", "W-09",
                    stillThere.Length == 0,
                    "四个下拉框都不存在",
                    stillThere.Length == 0 ? "全部已移除" : string.Join(",", stillThere));
            }
            finally { settingsWindow.Close(); }
        }
        catch (Exception ex)
        {
            _failed++;
            Cases.Add(new { name = "widgets-default-runner", ids = "W-01..W-06", driver = "WPF windows",
                expected = "no exception", actual = ex.ToString(), status = "Failed" });
        }
        finally
        {
            dock?.Close();
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

        Console.WriteLine($"小组件默认关闭：{Cases.Count - _failed} 通过，{_failed} 失败。报告：{report}");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Dock 里当前真正可见的小组件图标数量（读私有字段，避免为验收加生产接口）。</summary>
    private static int VisibleWidgetCount(DockWindow dock)
    {
        if (WidgetVisuals(dock) is not { } visuals) return -1;
        return visuals.Count(pair => pair.Host.Visibility == Visibility.Visible);
    }

    /// <summary>诊断用：总数 / 可见数 / 每个的 Kind 与可见性。</summary>
    private static string WidgetSummary(DockWindow dock)
    {
        if (WidgetVisuals(dock) is not { } visuals) return "读不到 _widgetVisuals";
        var parts = visuals.Select(pair => $"{pair.Kind}={(pair.Host.Visibility == Visibility.Visible ? "显示" : "隐藏")}");
        return $"总数 {visuals.Count}、可见 {visuals.Count(p => p.Host.Visibility == Visibility.Visible)}；{string.Join("，", parts)}";
    }

    private static List<(WidgetKind Kind, FrameworkElement Host)>? WidgetVisuals(DockWindow dock)
    {
        var field = typeof(DockWindow).GetField("_widgetVisuals", BindingFlags.Instance | BindingFlags.NonPublic);
        // 字段是 Dictionary<WidgetKind, WidgetVisual>：必须取 Values，
        // 直接遍历字典拿到的是 KeyValuePair，反射读 Kind 会静默落空。
        if (field?.GetValue(dock) is not IDictionary dictionary) return null;
        var list = new List<(WidgetKind, FrameworkElement)>();
        foreach (DictionaryEntry entry in dictionary)
        {
            var visual = entry.Value;
            if (visual is null) continue;
            var type = visual.GetType();
            var kind = type.GetProperty("Kind")?.GetValue(visual);
            var host = type.GetProperty("Host")?.GetValue(visual) as FrameworkElement;
            if (kind is WidgetKind widgetKind && host is not null) list.Add((widgetKind, host));
        }
        return list;
    }
}
