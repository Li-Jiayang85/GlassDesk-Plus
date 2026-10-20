using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// 原版 GlassDesk 4.x 的运行时补丁（通过 DOTNET_STARTUP_HOOKS 加载）。
/// <list type="number">
/// <item>移除「赞助支持」页与「检查更新 / 网盘下载」入口；</item>
/// <item>新增「桌面信息栏位置」设置：顶部居中（默认）/ 左上 / 右上 / 左下 / 右下。</item>
/// </list>
/// 只在 GlassDesk.exe 进程里生效；其它 .NET 进程加载后立即返回。
/// </summary>
public static class StartupHook
{
    private static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDeskPlusPatch");

    private static readonly string LogPath = Path.Combine(DataDirectory, "patch.log");
    private static readonly string ConfigPath = Path.Combine(DataDirectory, "config.json");

    private const string PositionKey = "infoBarPosition";
    private const string InfoBarWindowTitle = "信息栏";
    private const string SettingsAutomationName = "设置中心";

    private static readonly string[] PositionLabels =
        ["顶部居中", "左上角", "右上角", "左下角", "右下角"];

    private static readonly object Sync = new();
    private static bool _started;
    private static DispatcherTimer? _timer;
    private static string? _lastWindowList;
    private static SettingsPatch? _settingsPatch;
    private static string _position = "TopCenter";

    public static void Initialize()
    {
        try
        {
            if (!IsGlassDeskProcess()) return;
            lock (Sync)
            {
                if (_started) return;
                _started = true;
            }

            Directory.CreateDirectory(DataDirectory);
            _position = ReadPosition();
            Log($"hook loaded, pid={Environment.ProcessId}, position={_position}");
            InstallWindowShowHook();
            _ = WaitForApplication();
        }
        catch (Exception ex)
        {
            Log("Initialize failed: " + ex);
        }
    }

    private static bool IsGlassDeskProcess()
    {
        try
        {
            var path = Environment.ProcessPath ?? string.Empty;
            return Path.GetFileName(path).Equals("GlassDesk.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async System.Threading.Tasks.Task WaitForApplication()
    {
        for (var i = 0; i < 240; i++)
        {
            await System.Threading.Tasks.Task.Delay(250).ConfigureAwait(false);
            var app = Application.Current;
            if (app is null) continue;
            try
            {
                app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    _timer = new DispatcherTimer(DispatcherPriority.Send)
                    {
                        Interval = TimeSpan.FromMilliseconds(30),
                    };
                    _timer.Tick += (_, _) => Tick();
                    _timer.Start();
                    Log("dispatcher timer started (30ms positioning / 480ms settings)");
                }));
                return;
            }
            catch (Exception ex) { Log("begin invoke failed: " + ex.Message); }
        }
        Log("application never appeared");
    }

    private static int _slowTick;

    /// <summary>
    /// 每 30ms 跑一次：只做窗口定位（信息栏 / 控制中心），保证展开时几乎看不见跳动。
    /// 每 16 拍（约 480ms）才做设置页补丁与窗口日志，避免高频遍历可视树。
    /// </summary>
    private static void Tick()
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;

            var windows = app.Windows.Cast<Window>().Where(w => w.IsVisible).ToList();
            var runSlow = ++_slowTick % 16 == 0;

            Window? infoBar = null;
            Window? controlCenter = null;
            foreach (var window in windows)
            {
                if (window.Title.Contains(InfoBarWindowTitle, StringComparison.Ordinal))
                {
                    infoBar = window;
                    continue;
                }

                if (window.Title.Contains("控制中心", StringComparison.Ordinal))
                {
                    controlCenter = window;
                    continue;
                }

                var automationName = AutomationProperties.GetName(window) ?? string.Empty;
                if (automationName.Contains("启动台", StringComparison.Ordinal)
                    || window.Title.Contains("启动台", StringComparison.Ordinal))
                {
                    EnsureLauncherWheel(window);
                    EnsureLauncherSearchSync(window);
                }

                if (runSlow && automationName.Contains(SettingsAutomationName, StringComparison.Ordinal))
                {
                    ApplySettingsPatch(window);
                }
            }

            if (runSlow)
            {
                var list = string.Join(" | ", windows.Select(w => w.Title));
                if (list != _lastWindowList)
                {
                    _lastWindowList = list;
                    Log("windows: " + list);
                }
            }

            if (infoBar is not null)
            {
                ApplyInfoBarPosition(infoBar);
            }

            if (infoBar is not null && controlCenter is not null)
            {
                ApplyControlCenterPosition(controlCenter, infoBar);
            }
        }
        catch (Exception ex)
        {
            Log("tick failed: " + ex.Message);
        }
    }

    // ---------- 信息栏定位 ----------

    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultsToNearest = 0x00000002;
    private const int Margin = 22;

    private static void ApplyInfoBarPosition(Window bar)
    {
        try
        {
            var handle = new WindowInteropHelper(bar).Handle;
            if (handle == IntPtr.Zero) return;
            if (!GetWindowRect(handle, out var rect)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            var monitor = MonitorFromWindow(handle, MonitorDefaultsToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var area = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
                ? info.Work
                : new NativeRect { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };

            var (x, y) = _position switch
            {
                "TopLeft" => (area.Left + Margin, area.Top + Margin),
                "TopRight" => (area.Right - width - Margin, area.Top + Margin),
                "BottomLeft" => (area.Left + Margin, area.Bottom - height - Margin),
                "BottomRight" => (area.Right - width - Margin, area.Bottom - height - Margin),
                _ => (area.Left + (area.Right - area.Left - width) / 2, area.Top + Margin),
            };

            if (Math.Abs(rect.Left - x) <= 1 && Math.Abs(rect.Top - y) <= 1) return;
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            Log($"infobar -> {_position} ({x},{y})");
        }
        catch (Exception ex) { Log("position failed: " + ex.Message); }
    }

    /// <summary>控制中心是独立窗口：让它锚定到信息栏当前位置（右侧对齐，顶部位置在下方，底部位置在上方）。</summary>
    private static void ApplyControlCenterPosition(Window controlCenter, Window infoBar)
    {
        try
        {
            var ccHandle = new WindowInteropHelper(controlCenter).Handle;
            var barHandle = new WindowInteropHelper(infoBar).Handle;
            if (ccHandle == IntPtr.Zero || barHandle == IntPtr.Zero) return;
            if (!GetWindowRect(ccHandle, out var cc) || !GetWindowRect(barHandle, out var bar)) return;

            var ccWidth = cc.Right - cc.Left;
            var ccHeight = cc.Bottom - cc.Top;
            if (ccWidth <= 0 || ccHeight <= 0) return;

            var monitor = MonitorFromWindow(barHandle, MonitorDefaultsToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var area = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
                ? info.Work
                : new NativeRect { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };

            var atTop = (bar.Top + bar.Bottom) / 2 < (area.Top + area.Bottom) / 2;
            // 横向以信息栏中线对齐（控制中心中心 = 信息栏中心）。
            var barCenterX = (bar.Left + bar.Right) / 2;
            var x = barCenterX - ccWidth / 2;
            var y = atTop ? bar.Bottom + 12 : bar.Top - ccHeight - 12;
            x = Math.Clamp(x, area.Left + 8, area.Right - ccWidth - 8);
            y = Math.Clamp(y, area.Top + 8, area.Bottom - ccHeight - 8);

            if (Math.Abs(cc.Left - x) <= 1 && Math.Abs(cc.Top - y) <= 1) return;

            var deltaX = x - cc.Left;
            var deltaY = y - cc.Top;
            var shadows = FindShadowWindowsNear(cc);
            SetWindowPos(ccHandle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            foreach (var shadow in shadows)
            {
                var handle = new WindowInteropHelper(shadow).Handle;
                if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) continue;
                SetWindowPos(handle, IntPtr.Zero, rect.Left + deltaX, rect.Top + deltaY, 0, 0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate);
            }
            Log($"control center -> ({x},{y}) shadows={shadows.Count}");
        }
        catch (Exception ex) { Log("control center position failed: " + ex.Message); }
    }

    /// <summary>找到覆盖在控制中心周围的 ambient shadow 窗口，移动时带上它。</summary>
    private static List<Window> FindShadowWindowsNear(NativeRect rect)
    {
        var result = new List<Window>();
        var app = Application.Current;
        if (app is null) return result;
        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        foreach (Window window in app.Windows)
        {
            if (!window.Title.Contains("ambient shadow", StringComparison.OrdinalIgnoreCase)) continue;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var shadowRect)) continue;
            if (centerX >= shadowRect.Left && centerX <= shadowRect.Right
                && centerY >= shadowRect.Top && centerY <= shadowRect.Bottom)
            {
                result.Add(window);
            }
        }
        return result;
    }
    // ---------- 设置中心补丁 ----------

    private sealed class SettingsPatch
    {
        public ListBox? PageList;
        public ComboBox? PositionCombo;
        public bool EntriesHidden;
    }

    private static void ApplySettingsPatch(Window settings)
    {
        try
        {
            _settingsPatch ??= new SettingsPatch();
            var patch = _settingsPatch;

            if (patch.PageList is null)
            {
                patch.PageList = FindByName<ListBox>(settings, "SettingsPageList");
                if (patch.PageList is not null)
                {
                    Log($"page list found, items={patch.PageList.Items.Count}");
                }
            }

            if (patch.PageList is not null)
            {
                HideEntryPages(patch.PageList);
                HideUpdateControls(settings);
                HideSponsorSection(settings);
            }

            EnsurePositionCombo(settings);
            EnsureAboutExtras(settings);
            FixToggleHint(settings);
        }
        catch (Exception ex) { Log("settings patch failed: " + ex.Message); }
    }

    private static void HideEntryPages(ListBox pageList)
    {
        for (var i = 0; i < pageList.Items.Count; i++)
        {
            var item = pageList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (item is null) continue;
            var text = item.Content?.ToString() ?? TextOf(item);
            if (!text.Contains("赞助", StringComparison.Ordinal)) continue;
            item.Visibility = Visibility.Collapsed;
            item.Height = 0;
            item.MinHeight = 0;
            item.Margin = new Thickness(0);
            item.IsHitTestVisible = false;
            item.Focusable = false;
            Log($"hidden page: {text} (index {i})");
        }
    }
    private static void HideUpdateControls(DependencyObject root)
    {
        foreach (var button in FindAll<Button>(root))
        {
            if (button.Tag as string == "gd-patch-about") continue;
            var text = button.Content?.ToString() ?? TextOf(button);
            var automation = AutomationProperties.GetName(button) ?? string.Empty;
            if (text.Contains("检查更新", StringComparison.Ordinal)
                || text.Contains("网盘下载", StringComparison.Ordinal)
                || automation.Contains("检查更新", StringComparison.Ordinal))
            {
                Collapse(button);
                Log($"hidden update control: {text}/{automation}");
            }
        }
    }

    /// <summary>判断元素是否属于我们注入的「关于与致谢 / 赞助支持」块。</summary>
    private static bool IsInsidePatchBlock(DependencyObject node)
    {
        for (var depth = 0; depth < 6 && node is not null; depth++)
        {
            if (node is FrameworkElement element && element.Tag as string == "gd-patch-about") return true;
            node = VisualTreeHelper.GetParent(node)!;
        }
        return false;
    }

    /// <summary>关于页里的赞助说明、收款码按钮和支付方式选择一并隐藏。</summary>
    private static void HideSponsorSection(Window settings)
    {
        try
        {
            foreach (var block in FindAll<TextBlock>(settings))
            {
                if (IsInsidePatchBlock(block)) continue;
                var text = block.Text ?? string.Empty;
                if (!text.Contains("赞助", StringComparison.Ordinal)
                    && !text.Contains("收款码", StringComparison.Ordinal)
                    && !text.Contains("支付方式", StringComparison.Ordinal)
                    && !text.Contains("支付宝", StringComparison.Ordinal)
                    && !text.Contains("微信支付", StringComparison.Ordinal)
                    && !text.Contains("已是最新正式版本", StringComparison.Ordinal))
                {
                    continue;
                }

                if (block.Visibility != Visibility.Visible) continue;

                // 连同行容器一起收起；停在页面根 StackPanel 之前。
                UIElement target = block;
                var parent = VisualTreeHelper.GetParent(block);
                for (var depth = 0; depth < 3 && parent is not null; depth++)
                {
                    if (parent is System.Windows.Controls.Panel panel && panel.Children.Count > 2) break;
                    if (parent is UIElement parentElement) target = parentElement;
                    parent = VisualTreeHelper.GetParent(parent);
                }

                Collapse(target);
                Log("hidden sponsor block: " + Truncate(text));
            }
        }
        catch (Exception ex) { Log("hide sponsor failed: " + ex.Message); }
    }
    private static FrameworkElement? _positionRow;
    private static Panel? _positionRowHost;

    /// <summary>把「桌面信息栏位置」注入到 Dock 页「启用桌面信息栏」开关所在的分组里。</summary>
    private static void EnsurePositionCombo(Window settings)
    {
        try
        {
            if (_positionRow is not null && IsAttached(_positionRow, settings))
            {
                return;
            }

            var checkBox = FindByName<CheckBox>(settings, "ShowStatusBarCheckBox");
            if (checkBox is null) return;

            // 找到包住这个开关的分组 StackPanel（文案里含「启用桌面信息栏」的最深一层）。
            StackPanel? section = null;
            foreach (var panel in FindAll<StackPanel>(settings))
            {
                if (!TextOf(panel).Contains("启用桌面信息栏", StringComparison.Ordinal)) continue;
                section = panel;
            }
            if (section is null) return;

            var combo = CreatePositionCombo();
            var row = CreatePositionRow(combo);
            section.Children.Add(row);
            _positionRow = row;
            _positionRowHost = section;
            Log("position combo injected next to ShowStatusBarCheckBox");
        }
        catch (Exception ex) { Log("inject position combo failed: " + ex.Message); }
    }

    /// <summary>开关说明仍写着「在右上角显示」，位置可选后改成不写死角落。</summary>
    private static void FixToggleHint(Window settings)
    {
        foreach (var block in FindAll<TextBlock>(settings))
        {
            if (!(block.Text ?? string.Empty).Contains("在右上角显示时间", StringComparison.Ordinal)) continue;
            block.Text = "显示时间、系统状态和音乐；位置可在下方选择。关闭时同时收起相关弹窗，切换立即生效并自动保存。";
            Log("toggle hint updated");
        }
    }
    private static bool IsAttached(DependencyObject node, DependencyObject root)
    {
        var current = node;
        for (var guard = 0; guard < 64; guard++)
        {
            if (current == root) return true;
            current = VisualTreeHelper.GetParent(current);
            if (current is null) return false;
        }
        return false;
    }
    private static StackPanel? FindSettingsSidebar(DependencyObject root)
    {
        // 左侧栏 = 包含「设置分类」文本的 StackPanel。
        foreach (var panel in FindAll<StackPanel>(root))
        {
            var heading = panel.Children.OfType<TextBlock>()
                .Any(t => (t.Text ?? string.Empty).Contains("设置分类", StringComparison.Ordinal));
            if (heading) return panel;
        }
        return null;
    }

    private static ComboBox CreatePositionCombo()
    {
        var combo = new ComboBox
        {
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        AutomationProperties.SetName(combo, "桌面信息栏位置");
        foreach (var label in PositionLabels) combo.Items.Add(label);
        combo.SelectedIndex = Math.Max(0, Array.IndexOf(PositionLabels, LabelOf(_position)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            _position = KeyOf(PositionLabels[combo.SelectedIndex]);
            SavePosition(_position);
            Log($"position selected: {_position}");
        };
        return combo;
    }

    private static StackPanel CreatePositionRow(ComboBox combo)
    {
        var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(new TextBlock { Text = "桌面信息栏位置", FontWeight = FontWeights.SemiBold });
        row.Children.Add(combo);
        row.Children.Add(new TextBlock
        {
            Text = "顶部居中、左上角、右上角、左下角、右下角；修改后立即生效并保存。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return row;
    }

    private static string LabelOf(string key) => key switch
    {
        "TopLeft" => "左上角",
        "TopRight" => "右上角",
        "BottomLeft" => "左下角",
        "BottomRight" => "右下角",
        _ => "顶部居中",
    };

    private static string KeyOf(string label) => label switch
    {
        "左上角" => "TopLeft",
        "右上角" => "TopRight",
        "左下角" => "BottomLeft",
        "右下角" => "BottomRight",
        _ => "TopCenter",
    };

    private static string ReadPosition()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return "TopCenter";
            var node = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;
            if (node.TryGetProperty(PositionKey, out var value))
            {
                var text = value.GetString();
                if (text is not null && Array.IndexOf(PositionLabels, LabelOf(text)) >= 0) return text;
            }
        }
        catch (Exception ex) { Log("read config failed: " + ex.Message); }
        return "TopCenter";
    }

    private static void SavePosition(string position)
    {
        try
        {
            var payload = new Dictionary<string, string> { [PositionKey] = position };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log("save config failed: " + ex.Message); }
    }

    // ---------- 通用工具 ----------

    private static string TextOf(DependencyObject root)
    {
        var builder = new StringBuilder();
        foreach (var block in FindAll<TextBlock>(root))
        {
            builder.Append(block.Text);
        }
        return builder.ToString();
    }

    private static void Collapse(UIElement element)
    {
        element.Visibility = Visibility.Collapsed;
        if (element is FrameworkElement fe)
        {
            fe.Height = 0;
            fe.Margin = new Thickness(0);
        }
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (var element in FindAll<T>(root))
        {
            if (string.Equals(element.Name, name, StringComparison.Ordinal)) return element;
        }
        return null;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static string Truncate(string text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= 30 ? text : text[..30] + "…";

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private static WinEventDelegate? _showCallback;

    /// <summary>
    /// 在窗口「显示」事件里同步挪位：控制中心刚 Show 出来、还没绘制时就被放到正确位置，
    /// 配合 30ms 轮询兜底，用户看不到任何跳动。
    /// </summary>
    private static void InstallWindowShowHook()
    {
        try
        {
            _showCallback = OnWindowShow;
            var hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero,
                _showCallback, (uint)Environment.ProcessId, 0, WineventOutOfContext);
            Log(hook != IntPtr.Zero ? "win-event show hook installed" : "win-event show hook failed");
        }
        catch (Exception ex) { Log("install show hook failed: " + ex.Message); }
    }

    private static void OnWindowShow(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != ObjIdWindow || hwnd == IntPtr.Zero) return;

            var buffer = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            var title = buffer.ToString();
            if (!title.Contains("控制中心", StringComparison.Ordinal)) return;

            var app = Application.Current;
            if (app is null) return;
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => CorrectControlCenterImmediately(hwnd)));
                return;
            }

            CorrectControlCenterImmediately(hwnd);
        }
        catch { }
    }

    private static void CorrectControlCenterImmediately(IntPtr hwnd)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;

            Window? infoBar = null;
            Window? controlCenter = null;
            foreach (Window window in app.Windows)
            {
                if (window.Title.Contains(InfoBarWindowTitle, StringComparison.Ordinal)) infoBar = window;
                else if (new WindowInteropHelper(window).Handle == hwnd) controlCenter = window;
            }

            if (infoBar is null || controlCenter is null) return;
            ApplyControlCenterPosition(controlCenter, infoBar);
            Log("control center corrected on show-event");
        }
        catch (Exception ex) { Log("show-event correction failed: " + ex.Message); }
    }
    private static readonly HashSet<Window> WheelHooked = [];
    private static DateTime _lastWheelAt;

    /// <summary>启动台滚轮翻页：下滚下一页、上滚上一页，走它自带的 NextPage / PreviousPage 按钮。</summary>
    private static void EnsureLauncherWheel(Window launcher)
    {
        if (!WheelHooked.Add(launcher)) return;
        launcher.PreviewMouseWheel += OnLauncherWheel;
        Log("launcher wheel hooked");
    }

    private static void OnLauncherWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            if (e.Delta == 0 || sender is not Window launcher) return;

            var now = DateTime.UtcNow;
            if ((now - _lastWheelAt).TotalMilliseconds < 120)
            {
                e.Handled = true;
                return;
            }
            _lastWheelAt = now;

            var target = e.Delta < 0 ? "NextPage" : "PreviousPage";
            var button = FindPageButton(launcher, target);
            if (button is null || !button.IsEnabled)
            {
                e.Handled = true;
                return;
            }

            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            e.Handled = true;
            Log("launcher wheel -> " + target);
        }
        catch (Exception ex) { Log("launcher wheel failed: " + ex.Message); }
    }

    private static Button? FindPageButton(DependencyObject root, string name)
    {
        foreach (var button in FindAll<Button>(root))
        {
            if (button.Name == name) return button;
            if (AutomationProperties.GetAutomationId(button) == name) return button;
        }
        return null;
    }
    private static readonly HashSet<Window> SearchSyncHooked = [];

    /// <summary>
    /// 搜索框打字时刷新玻璃底图：清掉残留的 TransitionGlassLayer 旧帧，并请求一次 backdrop 刷新，
    /// 避免上一页图标在玻璃层里留下幽灵轮廓。
    /// </summary>
    private static void EnsureLauncherSearchSync(Window launcher)
    {
        if (!SearchSyncHooked.Add(launcher)) return;
        var box = FindByName<TextBox>(launcher, "SearchBox");
        if (box is null)
        {
            box = FindAll<TextBox>(launcher).FirstOrDefault();
        }
        if (box is null) return;
        box.TextChanged += (_, _) =>
        {
            var hasText = !string.IsNullOrEmpty(box.Text);
            SetLauncherCaptureExclusion(launcher, hasText);
            SyncLauncherGlass(launcher, "text-changed");
        };

        // 关闭/隐藏启动台时一定要把采集排除撤掉，否则下次截图里看不到它。
        launcher.IsVisibleChanged += (_, _) =>
        {
            if (!launcher.IsVisible) SetLauncherCaptureExclusion(launcher, false);
        };
        launcher.Closed += (_, _) => SetLauncherCaptureExclusion(launcher, false);
        Log("launcher search sync hooked");
    }

    private static void SyncLauncherGlass(Window launcher, string reason)
    {
        try
        {
            var cleared = 0;
            foreach (var element in FindAll<UIElement>(launcher))
            {
                var typeName = element.GetType().Name;
                if (typeName.Contains("TransitionGlassLayer", StringComparison.Ordinal))
                {
                    if (element.Opacity > 0 || element.Visibility != Visibility.Visible)
                    {
                        element.Opacity = 0;
                    }
                    cleared++;
                }

                else if (typeName.Contains("LiquidGlassSurface", StringComparison.Ordinal))
                {
                    TryInvokeGlassMethod(element, "CaptureBackdropScene");
                    TryInvokeGlassMethod(element, "RequestBackdropRefresh");
                    element.InvalidateVisual();
                }
            }
            Log($"glass sync ({reason}) transition-layers={cleared}");

        }
        catch (Exception ex) { Log("glass sync failed: " + ex.Message); }
    }
    private static int _lensDumps;

    /// <summary>诊断：打印 SharedGlassLens 实例上可调用的方法。</summary>
    private static void DumpLensApi(Window launcher)
    {
        if (_lensDumps++ > 1) return;
        try
        {
            foreach (var element in FindAll<DependencyObject>(launcher))
            {
                if (!element.GetType().Name.Contains("SharedGlassLens", StringComparison.Ordinal)) continue;
                var methods = element.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Refresh", StringComparison.Ordinal)
                        || m.Name.Contains("Capture", StringComparison.Ordinal)
                        || m.Name.Contains("Clear", StringComparison.Ordinal)
                        || m.Name.Contains("Reset", StringComparison.Ordinal)
                        || m.Name.Contains("Update", StringComparison.Ordinal)
                        || m.Name.Contains("Render", StringComparison.Ordinal)
                        || m.Name.Contains("Invalidate", StringComparison.Ordinal))
                    .Select(m => $"{m.Name}({m.GetParameters().Length})")
                    .Distinct().OrderBy(x => x);
                Log("lens api: " + string.Join(", ", methods));
            }
        }
        catch (Exception ex) { Log("dump lens api failed: " + ex.Message); }
    }
    private static int _typeDumps;

    /// <summary>诊断：列出启动台里出现的全部元素类型名。</summary>
    private static void DumpDistinctTypes(Window launcher)
    {
        if (_typeDumps++ > 1) return;
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            CollectTypes(launcher, names, 0);
            Log("distinct types: " + string.Join(", ", names.OrderBy(n => n)));
        }
        catch (Exception ex) { Log("dump types failed: " + ex.Message); }
    }

    private static void CollectTypes(DependencyObject node, HashSet<string> names, int depth)
    {
        if (depth > 20) return;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            names.Add(child.GetType().Name);
            CollectTypes(child, names, depth + 1);
        }
    }
    private static int _tileDumps;

    /// <summary>诊断：展开 TileGrid 的全部子项，看有没有残留的旧页格子。</summary>
    private static void DumpTileGrid(Window launcher)
    {
        if (_tileDumps++ > 1) return;
        try
        {
            FrameworkElement? grid = null;
            foreach (var element in FindAll<FrameworkElement>(launcher))
            {
                if (element.Name == "TileGrid") { grid = element; break; }
            }
            if (grid is null) { Log("tile grid not found"); return; }
            var lines = new List<string>();
            WalkChildren(grid, launcher, lines, 0);
            Log($"tile grid children: {lines.Count}");
            foreach (var line in lines.Take(40)) Log("  " + line);
        }
        catch (Exception ex) { Log("dump tile grid failed: " + ex.Message); }
    }

    private static void WalkChildren(DependencyObject node, Window launcher, List<string> lines, int depth)
    {
        if (depth > 4 || lines.Count >= 300) return;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            var desc = child.GetType().Name;
            if (child is FrameworkElement fe)
            {
                var bounds = fe.TransformToAncestor(launcher).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                desc += $" name='{fe.Name}' {bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0} op={fe.Opacity:0.00} vis={fe.Visibility}";
            }
            lines.Add($"{new string(' ', depth)}{desc}");
            WalkChildren(child, launcher, lines, depth + 1);
        }
    }
    private static int _clipDumps;

    /// <summary>诊断：展开 ContainerTransformClipRoot，找出残留的旧帧快照。</summary>
    private static void DumpTransformClipRoot(Window launcher)
    {
        if (_clipDumps++ > 1) return;
        try
        {
            FrameworkElement? root = null;
            foreach (var element in FindAll<FrameworkElement>(launcher))
            {
                if (element.Name == "ContainerTransformClipRoot") { root = element; break; }
            }
            if (root is null) { Log("clip root not found"); return; }
            var lines = new List<string>();
            WalkAll(root, lines, 0);
            Log($"clip root tree: {lines.Count}");
            foreach (var line in lines.Take(50)) Log("  " + line);
        }
        catch (Exception ex) { Log("dump clip root failed: " + ex.Message); }
    }

    private static void WalkAll(DependencyObject node, List<string> lines, int depth)
    {
        if (depth > 6 || lines.Count >= 300) return;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            var name = child is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? $" name='{fe.Name}'" : string.Empty;
            var size = child is FrameworkElement f2 ? $" {f2.ActualWidth:0}x{f2.ActualHeight:0} op={f2.Opacity:0.00} vis={f2.Visibility}" : string.Empty;
            lines.Add($"{new string(' ', depth)}{child.GetType().Name}{name}{size}");
            WalkAll(child, lines, depth + 1);
        }
    }
    private static int _ghostDumps;

    /// <summary>诊断：列出落在幽灵区域（结果下方空区）里仍在绘制的元素。</summary>
    private static void DumpGhostAreaElements(Window launcher)
    {
        if (_ghostDumps++ > 1) return;
        try
        {
            var area = new Rect(100, 380, 1300, 420);
            var lines = new List<string>();
            foreach (var element in FindAll<FrameworkElement>(launcher))
            {
                if (element.Visibility != Visibility.Visible || element.Opacity <= 0.01) continue;
                if (element.ActualWidth < 10 || element.ActualHeight < 10) continue;
                try
                {
                    var bounds = element.TransformToAncestor(launcher)
                        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (!bounds.IntersectsWith(area)) continue;
                    lines.Add($"{element.GetType().Name} name='{element.Name}' {bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0} op={element.Opacity:0.00}");
                }
                catch { }
            }
            Log($"ghost-area elements: {lines.Count}");
            foreach (var line in lines.Take(30)) Log("  " + line);
        }
        catch (Exception ex) { Log("dump ghost area failed: " + ex.Message); }
    }
    private static int _translucentDumps;

    /// <summary>诊断：列出打字后仍处于半透明状态的元素，找出幽灵图标的来源。</summary>
    private static void DumpTranslucentLauncherElements(Window launcher)
    {
        if (_translucentDumps++ > 2) return;
        try
        {
            var lines = new List<string>();
            foreach (var element in FindAll<FrameworkElement>(launcher))
            {
                if (element.Opacity <= 0.05 || element.Opacity >= 0.95) continue;
                if (element.ActualWidth < 30 || element.ActualHeight < 30) continue;
                lines.Add($"{element.GetType().Name} name='{element.Name}' opacity={element.Opacity:0.00} size={element.ActualWidth:0}x{element.ActualHeight:0} vis={element.Visibility}");
            }
            Log($"translucent elements: {lines.Count}");
            foreach (var line in lines.Take(25)) Log("  " + line);
        }
        catch (Exception ex) { Log("dump translucent failed: " + ex.Message); }
    }
    private static int _containerDumps;

    /// <summary>诊断：打字后打印启动台里的大块容器，定位结果层与底层网格。</summary>
    private static void DumpLauncherContainers(Window launcher)
    {
        if (_containerDumps++ > 2) return;
        try
        {
            var lines = new List<string>();
            WalkContainers(launcher, lines, 0);
            Log($"containers: {lines.Count}");
            foreach (var line in lines.Take(40)) Log("  " + line);
        }
        catch (Exception ex) { Log("dump containers failed: " + ex.Message); }
    }

    private static void WalkContainers(DependencyObject node, List<string> lines, int depth)
    {
        if (depth > 10 || lines.Count >= 200) return;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && (fe.ActualWidth >= 400 || fe.ActualHeight >= 400))
            {
                var name = string.IsNullOrEmpty(fe.Name) ? string.Empty : $" name='{fe.Name}'";
                lines.Add($"{new string(' ', depth)}{fe.GetType().Name}{name} {fe.ActualWidth:0}x{fe.ActualHeight:0} vis={fe.Visibility} op={fe.Opacity:0.00}");
            }
            WalkContainers(child, lines, depth + 1);
        }
    }
    private static int _apiDumps;

    /// <summary>诊断：打印玻璃表面实例上可调用的刷新类方法（名称+参数个数）。</summary>
    private static void DumpSurfaceApi(Window launcher)
    {
        if (_apiDumps++ > 1) return;
        try
        {
            foreach (var element in FindAll<FrameworkElement>(launcher))
            {
                if (!element.GetType().Name.Contains("LiquidGlassSurface", StringComparison.Ordinal)) continue;
                var methods = element.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Refresh", StringComparison.Ordinal)
                        || m.Name.Contains("Capture", StringComparison.Ordinal)
                        || m.Name.Contains("Scene", StringComparison.Ordinal)
                        || m.Name.Contains("Invalidate", StringComparison.Ordinal))
                    .Select(m => $"{m.Name}({m.GetParameters().Length})")
                    .Distinct()
                    .OrderBy(x => x);
                Log("surface api: " + string.Join(", ", methods));
            }
        }
        catch (Exception ex) { Log("dump api failed: " + ex.Message); }
    }
    private static int _glassInvokeLogs;

    /// <summary>调用玻璃表面上的刷新方法，参数按签名补默认值（bool=true / 枚举=1 / 其它 default）。</summary>
    private static void TryInvokeGlassMethod(object target, string name)
    {
        try
        {
            var method = target.GetType().GetMethod(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method is null) return;

            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                args[i] = type == typeof(bool) ? (object)true
                    : type.IsEnum ? Enum.ToObject(type, 1)
                    : type.IsValueType ? Activator.CreateInstance(type)
                    : null;
            }

            method.Invoke(target, args);
            if (_glassInvokeLogs++ < 10) Log($"glass invoke {name}({parameters.Length}) ok");
        }
        catch (Exception ex)
        {
            if (_glassInvokeLogs++ < 10) Log($"glass invoke {name} failed: {ex.GetBaseException().Message}");
        }
    }
    private static readonly Dictionary<nint, bool> CaptureExclusionState = [];

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>
    /// 搜索时临时把启动台排除在采集之外（否则玻璃底图会拍到它自己的上一页内容，留下幽灵），
    /// 清空搜索后立即恢复，保证日常截图/录屏仍能看到启动台。
    /// </summary>
    private static void SetLauncherCaptureExclusion(Window launcher, bool exclude)
    {
        try
        {
            var handle = new WindowInteropHelper(launcher).Handle;
            if (handle == IntPtr.Zero) return;

            if (CaptureExclusionState.TryGetValue(handle, out var current) && current == exclude) return;

            var ok = SetWindowDisplayAffinity(handle, exclude ? WdaExcludeFromCapture : WdaNone);
            CaptureExclusionState[handle] = exclude;
            Log($"launcher capture exclusion={exclude}: {ok}");
        }
        catch (Exception ex) { Log("capture exclusion failed: " + ex.Message); }
    }
    // ---------- 关于页：致谢 / 赞助 / 更新 ----------

    /// <summary>补丁自身版本，用于和自己仓库的最新 Release 比较。</summary>
    private const string PatchVersion = "1.1.0";

    /// <summary>补丁源码与更新仓库。</summary>
    private const string UpdateRepo = "Li-Jiayang85/GlassDesk-Plus-Patch";
    private const string GitHubProfile = "github.com/Li-Jiayang85";

    private const string DouyinAccount = "喜羊羊（远程电脑技术支持）";
    private const string PatchAssetName = "GlassDeskPatch.dll";

    private static bool _updateChecking;


    /// <summary>在关于页追加「项目信息与致谢」和「赞助支持」（含微信/支付宝收款码）。</summary>
    private static readonly List<FrameworkElement> AboutExtras = [];
    private static int _aboutDebug;

    private static void EnsureAboutExtras(Window settings)
    {
        try
        {
            var panel = FindPageContentPanel(settings);
            var aboutVisible = panel is not null && IsText(panel, "关于与运行信息", "GlassDesk 当前版本");
            if (_aboutDebug++ < 5)
            {
                var sample = panel is null ? "<null>" : TextOf(panel);
                if (sample.Length > 80) sample = sample[..80];
                Log($"about debug: panel={(panel is null ? "null" : "ok")} children={panel?.Children.Count ?? -1} visible={aboutVisible} text={sample}");
            }

            AboutExtras.RemoveAll(e => !IsAttached(e, settings));

            if (panel is not null && aboutVisible && AboutExtras.Count == 0)
            {
                var info = BuildAboutSection();
                var sponsor = BuildSponsorSection();
                panel.Children.Add(info);
                panel.Children.Add(sponsor);
                AboutExtras.Add(info);
                AboutExtras.Add(sponsor);
                Log("about extras injected");
            }

            foreach (var extra in AboutExtras)
            {
                extra.Visibility = aboutVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex) { Log("inject about extras failed: " + ex.Message); }
    }

    private static StackPanel? FindPageContentPanel(Window settings)
    {
        foreach (var scroll in FindAll<ScrollViewer>(settings))
        {
            if (!string.Equals(scroll.Name, "SettingsScroll", StringComparison.Ordinal)) continue;
            var presenter = FindAll<ScrollContentPresenter>(scroll).FirstOrDefault();
            if (presenter is not null)
            {
                foreach (var panel in FindAll<StackPanel>(presenter)) return panel;
            }
        }
        return null;
    }

    private static bool IsText(DependencyObject root, params string[] needles)
    {
        var text = TextOf(root);
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>更新用的 HttpClient：DNS 正常时直连，被污染（解析到 127.0.0.1）时走 GitHub 已知 IP 兜底。</summary>
    private static System.Net.Http.HttpClient CreateUpdateClient()
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, token) =>
            {
                try
                {
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    foreach (var ip in new[] { "140.82.112.6", "140.82.113.6", "140.82.114.6", "20.205.243.168" })
                    {
                        try
                        {
                            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                            await socket.ConnectAsync(System.Net.IPAddress.Parse(ip), 443, token).ConfigureAwait(false);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                        catch { }
                    }
                    throw;
                }
            },
        };

        var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassDeskPlusPatch/" + PatchVersion);
        return client;
    }

    private static StackPanel BuildAboutSection()
    {
        var panel = new StackPanel { Tag = "gd-patch-about", Margin = new Thickness(0, 20, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "关于与致谢",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var lines = new[]
        {
            "原始程序：GlassDesk（原作者 everlosttime，Apache-2.0 许可证）",
            "维护与发布：喜羊羊（远程电脑技术支持）",
            "抖音账号：" + DouyinAccount,
            $"软件版本：v{PatchVersion}",
            "GitHub：" + GitHubProfile,
        };
        foreach (var line in lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Opacity = 0.85,
            });
        }

        var checkButton = new Button
        {
            Content = "检查更新",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(16, 6, 16, 6),
        };
        checkButton.Tag = "gd-patch-about";
        checkButton.Click += (_, _) => CheckPatchUpdate();
        panel.Children.Add(checkButton);

        return panel;
    }

    private static StackPanel BuildSponsorSection()
    {
        var panel = new StackPanel { Tag = "gd-patch-about", Margin = new Thickness(0, 22, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "赞助支持",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "软件免费；如果它帮到了你，可以自愿赞助，金额随意。扫码后手机端确认即可。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(BuildQr("微信", "wechat.jpg"));
        row.Children.Add(BuildQr("支付宝", "alipay.jpg"));
        panel.Children.Add(row);

        return panel;
    }

    private static StackPanel BuildQr(string label, string fileName)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 28, 0) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), Opacity = 0.85 });

        var path = Path.Combine(DataDirectory, fileName);
        if (File.Exists(path))
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                panel.Children.Add(new Image
                {
                    Source = bitmap,
                    Width = 190,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                });
            }
            catch (Exception ex) { Log("load qr failed: " + ex.Message); }
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "（收款码图片缺失）", Opacity = 0.6 });
        }

        return panel;
    }

    private static void CheckPatchUpdate()
    {
        if (_updateChecking) return;
        _updateChecking = true;

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var http = CreateUpdateClient();

                var json = await http.GetStringAsync($"https://api.github.com/repos/{UpdateRepo}/releases/latest")
                    .ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
                Log("update remote tag=" + tag);

                if (ParseVersion(tag) <= ParseVersion("v" + PatchVersion))
                {
                    ShowMessage($"当前已是最新版本（v{PatchVersion}）。", "检查更新");
                    return;
                }

                string? url = null;
                string? installerName = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    url = asset.GetProperty("browser_download_url").GetString();
                    installerName = name;
                    break;
                }

                if (url is null || installerName is null)
                {
                    ShowMessage($"发现新版本 {tag}，但该版本没有附带安装包。", "检查更新");
                    return;
                }

                var yes = ShowQuestion($"发现新版本 {tag}（当前 v{PatchVersion}）。\n\n现在下载并安装更新吗？更新会自动重启 GlassDesk。", "检查更新");
                if (!yes) return;

                var installerPath = Path.Combine(Path.GetTempPath(), installerName);
                var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(installerPath, bytes).ConfigureAwait(false);

                InstallUpdate(installerPath);
            }
            catch (Exception ex)
            {
                ShowMessage("检查更新失败：" + ex.Message, "检查更新");
            }
            finally
            {
                _updateChecking = false;
            }
        });
    }

    private static Version ParseVersion(string tag)
    {
        var digits = new string((tag ?? string.Empty).Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }

    /// <summary>启动下载好的安装包做静默升级，然后退出当前程序（安装器会在结束时重新拉起 GlassDesk）。</summary>
    private static void InstallUpdate(string installerPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                installerPath, "/SILENT /NORESTART /SUPPRESSMSGBOXES")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowMessage("启动安装程序失败：" + ex.Message, "检查更新");
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Application.Current?.Shutdown()));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbYesNo = 0x00000004;
    private const int IdYes = 6;

    private static void ShowMessage(string text, string title)
    {
        try { MessageBoxW(IntPtr.Zero, text, title, MbIconInformation); }
        catch (Exception ex) { Log("show message failed: " + ex.Message); }
    }

    private static bool ShowQuestion(string text, string title)
    {
        try { return MessageBoxW(IntPtr.Zero, text, title, MbYesNo | MbIconQuestion) == IdYes; }
        catch (Exception ex) { Log("show question failed: " + ex.Message); return false; }
    }
    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}
