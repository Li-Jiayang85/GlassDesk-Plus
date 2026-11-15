using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class AppearanceSliderDragTests
{
    private static double Min(double value) => Math.Clamp(value, 0d, 1d);

    [Fact]
    public void DraggingDefersPersistenceUntilTheThumbIsReleased()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-slider-drag-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var settings = new SettingsWindow();
            try
            {
                settings.AttachDataStore(data, null);
                _ = new WindowInteropHelper(settings).EnsureHandle();
                var slider = (Slider)settings.FindName("StatusBarTransparencySlider");
                var before = data.Settings.Current.Appearance.StatusBarGlassTransparency ?? 0d;

                slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                foreach (var value in new[] { Min(before + .1), Min(before + .2), Min(before + .3) })
                {
                    slider.Value = value;
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                }

                // 拖动过程中只更新内存里的值，磁盘上的设置保持不变。
                var duringDrag = new SettingsStore(data.Settings.FilePath); duringDrag.Load();
                Assert.Equal(before, duringDrag.Current.Appearance.StatusBarGlassTransparency);
                var dragged = Min(before + .3);
                Assert.Equal(dragged, data.Settings.Current.Appearance.StatusBarGlassTransparency);

                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);

                // 松手后一次性落盘，最终值写入设置文件。
                var afterRelease = new SettingsStore(data.Settings.FilePath); afterRelease.Load();
                Assert.Equal(dragged, afterRelease.Current.Appearance.StatusBarGlassTransparency);
            }
            finally { settings.Close(); Directory.Delete(root, true); }
        });
    }
}
