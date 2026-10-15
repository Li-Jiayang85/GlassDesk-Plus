using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI;
using GlassDesk.UI.Controls;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using Microsoft.Win32;

internal static partial class Program
{
    private static void VerifyDesktopIconToggleContinuity(object host, Type hostType, DockWindow dock,
        LauncherWindow launcher, SettingsWindow settings, string evidence, string? fixtureExe)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var generationProperty = typeof(BackdropRecovery).GetProperty("Generation", BindingFlags.Static | BindingFlags.NonPublic)!;
        long Generation() => (long)generationProperty.GetValue(null)!;
        var surface = (LiquidGlassSurface)dock.FindName("GlassSurface")!;
        var frameField = typeof(LiquidGlassSurface).GetField("_frame", fields)!;
        var transitionField = typeof(LiquidGlassSurface).GetField("_transitionBackdrop", fields)!;
        var readyField = typeof(LiquidGlassSurface).GetField("_lastRenderMaterialReady", fields)!;
        var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
        var sceneField = typeof(LiquidGlassSurface).GetField("_backdropScene", fields)!;
        var stampField = typeof(LiquidGlassSurface).GetField("_materialStamp", fields)!;
        var stampReader = typeof(LiquidGlassSurface).GetMethod("ReadMaterialStamp", fields)!;
        string? Scene()
        {
            var scene = sceneField.GetValue(surface);
            if (scene is null) return null;
            var windows = (Array)scene.GetType().GetField("_windows", fields)!.GetValue(scene)!;
            return string.Join(";", windows.Cast<object>().Select(w => w.ToString()));
        }
        bool HasLive() => frameField.GetValue(surface) is not null || transitionField.GetValue(surface) is not null;
        var preferences = new List<object>();
        var observations = new List<object>();
        var missing = 0;
        var black = 0;
        var samples = 0;
        var phase = "baseline";
        UserPreferenceChangedEventHandler preference = (_, e) => preferences.Add(new
            { atTicks = Stopwatch.GetTimestamp(), category = e.Category.ToString(), generation = Generation() });
        EventHandler observe = (_, _) =>
        {
            var hasLive = HasLive();
            var ready = readyField.GetValue(surface) is true;
            var pixels = background.LastCaptureTiming?.PixelStatistics;
            var pixelStats = pixels is null ? default(JsonElement?) : JsonSerializer.SerializeToElement(pixels);
            var nearBlack = pixelStats?.GetProperty("nearBlackFraction").GetDouble() ?? 0;
            observations.Add(new { phase, atTicks = Stopwatch.GetTimestamp(), generation = Generation(),
                ready, hasLive, source = surface.BackdropSource.ToString(), pixelStats, scene = Scene(),
                savedStamp = stampField.GetValue(surface)?.ToString(), currentStamp = stampReader.Invoke(surface, null)?.ToString() });
            if (phase != "baseline")
            {
                samples++;
                if (!hasLive || !ready) missing++;
                if (nearBlack > .98) black++;
            }
        };
        var before = settings.DesktopIconsRead?.Invoke();
        if (!before.HasValue) throw new InvalidOperationException("Desktop icon state unavailable.");
        hostType.GetMethod("ShowLauncher", fields, null, Type.EmptyTypes, null)!.Invoke(host, null);
        Check("切换前 Dock 完整实时玻璃已就绪", "desktop-icons/material",
            PumpUntil(HasLive, TimeSpan.FromSeconds(6)), "live material", surface.BackdropSource.ToString());
        Pump(TimeSpan.FromMilliseconds(500));
        var firstGeneration = Generation();
        var fixtures = new List<Process>();
        SystemEvents.UserPreferenceChanged += preference;
        CompositionTarget.Rendering += observe;
        FirstFrameRecording.DockContinuityRecording? desktopRecording = null;
        try
        {
            Pump(TimeSpan.FromMilliseconds(300));
            var button = (Button)dock.FindName("DesktopIconsButton")!;
            var recordingRegion = DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(dock).Handle);
            desktopRecording = new(recordingRegion!.Value, () => phase, Path.Combine(evidence, "composed-dock"));
            for (var i = 0; i < 6; i++)
            {
                phase = "toggle-" + i;
                if (i == 0)
                    FirstFrameRecording.Record(dock, () => button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent)),
                        Path.Combine(evidence, "first-toggle"), TimeSpan.FromSeconds(1.5), recordingRegion);
                else
                {
                    button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                    Pump(TimeSpan.FromMilliseconds(700));
                }
                Check("桌面图标显隐切换 " + i, "desktop-icons/action",
                    settings.DesktopIconsRead?.Invoke() == (i % 2 == 0 ? !before.Value : before.Value),
                    "expected icon state", settings.DesktopIconsRead?.Invoke()?.ToString() ?? "null");
            }
            if (fixtureExe is not null)
            {
                for (var i = 0; i < 3; i++)
                {
                    phase = "window-open-" + i;
                    var readyPath = Path.Combine(evidence, "window-fixture-" + i + ".json");
                    var start = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add("--desktop-transition-fixture"); start.ArgumentList.Add(readyPath);
                    var process = Process.Start(start)!; fixtures.Add(process);
                    if (!PumpUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(5))) throw new TimeoutException("Window fixture did not start.");
                    using var fixture = JsonDocument.Parse(File.ReadAllText(readyPath));
                    var handle = (nint)fixture.RootElement.GetProperty("windowHandle").GetInt64();
                    Pump(TimeSpan.FromMilliseconds(500));
                    var originalBounds = DisplayService.GetWindowPixelBounds(handle)!.Value;
                    phase = "window-move-" + i;
                    for (var step = 0; step < 6; step++)
                    {
                        DisplayService.MoveWindowToPixelRect(handle, originalBounds with { X = originalBounds.X + step * 6 });
                        Pump(TimeSpan.FromMilliseconds(45));
                    }
                    phase = "window-resize-" + i;
                    for (var step = 0; step < 6; step++)
                    {
                        DisplayService.MoveWindowToPixelRect(handle, originalBounds with { Width = originalBounds.Width - step * 8 });
                        Pump(TimeSpan.FromMilliseconds(45));
                    }
                    phase = "window-minimize-" + i;
                    WindowControl.Minimize(handle); Pump(TimeSpan.FromMilliseconds(500));
                    phase = "window-restore-" + i;
                    WindowControl.Restore(handle); Pump(TimeSpan.FromMilliseconds(500));
                    phase = "window-close-" + i;
                    if (i == 0)
                        FirstFrameRecording.Record(dock, () => PostDesktopFixtureClose(handle, 0x0010, 0, 0),
                            Path.Combine(evidence, "first-window-close"), TimeSpan.FromSeconds(1.5), recordingRegion);
                    else PostDesktopFixtureClose(handle, 0x0010, 0, 0);
                    if (!PumpUntil(() => process.HasExited, TimeSpan.FromSeconds(5))) throw new TimeoutException("Window fixture did not close.");
                    Pump(TimeSpan.FromMilliseconds(700));
                }
            }
            Check("图标显隐不触发整个背景采集重建", "desktop-icons/recovery",
                Generation() == firstGeneration, firstGeneration.ToString(), Generation().ToString());
            desktopRecording.Dispose();
            Check("实际桌面上的 Dock 连续操作无黑闪", "desktop-icons/material",
                desktopRecording.SampleCount > 0 && desktopRecording.WaitingBodySamples == 0 && desktopRecording.Failure is null
                    && samples > 0 && missing == 0,
                "0 waiting-body desktop samples and no lost live textures",
                $"waiting={desktopRecording.WaitingBodySamples}/{desktopRecording.SampleCount}; cache={missing}/{samples}; " + desktopRecording.Failure);
            desktopRecording = null;
            Check("图标显隐期间采集不出现整片黑帧", "desktop-icons/pixels", samples > 0 && black == 0,
                "0 black samples", $"{black}/{samples}");
        }
        finally
        {
            desktopRecording?.Dispose();
            CompositionTarget.Rendering -= observe;
            SystemEvents.UserPreferenceChanged -= preference;
            foreach (var fixture in fixtures) { if (!fixture.HasExited) fixture.Kill(); fixture.Dispose(); }
            settings.DesktopIconsSet?.Invoke(before.Value);
            BackdropRecovery.ExportDiagnosticReport(Path.Combine(evidence, "backdrop-diagnostics.json"));
            File.WriteAllText(Path.Combine(evidence, "desktop-icon-continuity.json"), JsonSerializer.Serialize(new
            {
                note = "Actual AppHost Dock route, original icon state restored. Composition samples are scheduling diagnostics, not displayed FPS.",
                firstGeneration, lastGeneration = Generation(), samples, missing, black, preferences, observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostDesktopFixtureClose(nint window, uint message, nint wParam, nint lParam);
}
