using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private static readonly CultureInfo WidgetCulture = CultureInfo.GetCultureInfo("zh-CN");

    // 节拍取 250ms 而不是 500ms：时钟要 0.5 秒刷新一次，节拍等于刷新周期时
    // 每一拍都可能差几毫秒而判成「还没到」，实际会退化成一秒一跳。
    private readonly DispatcherTimer _widgetsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<WidgetKind, WidgetVisual> _widgetVisuals = [];
    private bool _widgetsInitialized;
    private WidgetKind? _activePanelKind;

    // 面板展开/收起的动画状态。只动 Opacity 与这个缩放——两者都不参与布局，
    // 因此不会引起逐帧重排（Dock 是分层玻璃窗口，逐帧改尺寸会非常卡）。
    private readonly ScaleTransform _widgetPanelScale = new(1, 1);
    private int _widgetPanelReveal;

    // 小组件面板现在是独立弹窗（见 WidgetPopupWindow）：Dock 自身不再改变尺寸，
    // 所以既没有"长高"的跳动，也不打扰 Dock 的玻璃捕获。
    // Dock 里那个内联面板元素保留但永不显示——它承载的内容构建函数仍被弹窗复用。
    private WidgetPopupWindow? _widgetPopup;

    /// <summary>
    /// 当前打开的面板里那一个会变的文字元素（时钟/日期/监控/日历用）。
    /// </summary>
    /// <remarks>
    /// 有了它，定时刷新只需改文字，不必重建整棵内容树——原先每 500ms 重建一次，
    /// 实测让工作集在 25 分钟内涨约 113 MB、句柄 +178。
    /// </remarks>
    private TextBlock? _activePanelDetail;

    /// <summary>收起时的缩放：面板从略小长到原大小，手感接近系统控制中心。</summary>
    private const double WidgetPanelRestScale = .94;

    /// <summary>面板展开后的高度上限，与 XAML 里的 MaxHeight 一致；展开动画以它为目标值。</summary>
    private const double WidgetPanelMaxHeightDip = 340;
    private static readonly Duration WidgetPanelRevealDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration WidgetPanelHideDuration = new(TimeSpan.FromMilliseconds(140));

    // 备忘录和待办数据持久化
    private static readonly string NotesPath = GetSettingsPath("notes.json");
    private static readonly string TodoPath = GetSettingsPath("todo.json");
    private static readonly JsonSerializerOptions NotesJsonOptions = new() { WriteIndented = true };
    private static List<NoteItem> _notes = [];
    private static List<TodoItem> _todos = [];
    private int? _editingNoteId;

    private DateTime _lastCpuSampleTime;
    private long _lastCpuIdle, _lastCpuKernel, _lastCpuUser;

    /// <summary>
    /// 天气数据与地理编码共用的客户端。
    /// </summary>
    /// <remarks>
    /// **必须带 User-Agent**：地理编码用的是 nominatim，它的使用政策要求请求方标识自己，
    /// 缺了这个头会被直接拒绝——这正是"输入城市却一直定位不过去"的原因。
    /// Accept-Language 让地名按中文返回；超时放宽到 8 秒（地理编码比天气接口慢）。
    /// </remarks>
    private static readonly HttpClient WeatherHttp = CreateWeatherHttp();

    private static HttpClient CreateWeatherHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "GlassDeskPlus/1.0 (+https://github.com/Li-Jiayang85/GlassDesk-Plus)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }
    private WeatherSnapshot _weather = new();
    private bool _weatherFetching;
    private bool _weatherLocateStarted;
    private double _weatherLat, _weatherLon;
    private string _weatherLocation = "";

    private sealed class WidgetVisual
    {
        public required WidgetKind Kind { get; init; }
        public required Button Host { get; init; }
        // 可写：面板内容每次重建都会新建一批按钮（不能跨次复用元素实例，见 BuildAudioBody），
        // 状态刷新要作用到"当前这一批"上。
        public MediaControlButtons? MediaControls { get; set; }
        public WidgetSettings? Settings { get; set; }
        public DateTime NextRefreshUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class MediaControlButtons
    {
        public required Button Prev { get; init; }
        public required Button Play { get; init; }
        public required Button Next { get; init; }
    }

    // 备忘录和待办数据模型
    private sealed class NoteItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private sealed class TodoItem
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public bool Done { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class WeatherSnapshot
    {
        public bool Ready;
        public bool Offline = true;
        public double TemperatureC;
        public string Condition = "";
        public string Location = "";
        public DateTime FetchedUtc;
    }

    private static string GetSettingsPath(string fileName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "GlassDesk", fileName);
    }

    /// <summary>给标准 Button 套用 PhoneBorder 连续圆角模板，实现 iOS 风格圆角按钮。</summary>
    private static void ApplyRoundedChrome(Button button, double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(PhoneBorder));
        border.SetValue(PhoneBorder.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
    }

    /// <summary>创建小组件图标入口（WidgetStripHost，TileStripHost 之后）：开启后 Dock 立即显示对应图标，点击内联面板。</summary>
    private void InitializeWidgets()
    {
        if (_widgetsInitialized) return;
        _widgetsInitialized = true;

        // 面板从底部中心长出（贴着 Dock 往上展开），并挂上动画用的缩放。
        DockWidgetPanel.RenderTransformOrigin = new Point(.5, 1);
        DockWidgetPanel.RenderTransform = _widgetPanelScale;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var kind in WidgetKinds.Available)
        {
            var (iconData, name) = GetWidgetIdentity(kind);

            // 与固定入口同一套规格：48×48 的描边矢量图标，颜色跟随窗口前景色。
            // 原先这里是彩色 emoji（Segoe UI Emoji）加下方 9pt 文字标题，
            // 既不单色、尺寸与结构也与旁边那排入口不一致。
            var content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(BuildWidgetIcon(iconData));

            var host = new Button
            {
                Width = 48,
                Height = 64,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = content,
                ToolTip = $"{name} · 点击查看",
                Visibility = Visibility.Collapsed,
            };

            // 音频控制按钮不在这里创建：面板内容每次重建都要新建一批，
            // 跨次复用同一个元素实例会在第二次挂载时抛异常（详见 BuildAudioBody）。
            var visual = new WidgetVisual
            {
                Kind = kind,
                Host = host,
            };
            host.Click += (_, _) => ToggleWidgetPanel(visual);

            _widgetVisuals[kind] = visual;
            panel.Children.Add(host);
        }

        WidgetStripHost.Content = panel;

        // AppHost 在创建任何窗口前已 Initialize 并推送初始设置；这里只订阅变更。
        // 若宿主尚未就绪也不阻塞图标显示：RefreshWidgets 会按默认配置兜底。
        var widgetHost = WidgetSettingsHost.Current;
        if (widgetHost is not null)
        {
            widgetHost.Changed += OnWidgetSettingsChanged;
        }
        // 定时器 Tick 里逃出的异常会直接崩掉进程，而音频/天气的数据源来自系统组件、
        // 随时可能失效，所以在这里整体兜住。
        _widgetsTimer.Tick += (_, _) =>
        {
            try
            {
                TickWidgets();
            }
            catch (Exception)
            {
            }
        };
        IsVisibleChanged += (_, _) => UpdateWidgetTimerState();
        PerformanceSurfaceVisibilityChanged += (_, _) => UpdateWidgetTimerState();

        RefreshWidgets();
        UpdateWidgetTimerState();
        _ = LoadDataAsync();
    }

    private void OnWidgetSettingsChanged(object? sender, EventArgs e)
    {
        RefreshWidgets();
        UpdateWidgetTimerState();
    }

    /// <summary>按当前设置重建小组件可见性（开启且停靠 Dock 的才显示为图标入口）。</summary>
    private void RefreshWidgets()
    {
        var host = WidgetSettingsHost.Current;
        var enabled = host?.Enabled ?? true;

        foreach (var visual in _widgetVisuals.Values)
        {
            visual.Settings = enabled
                ? host?.Items.FirstOrDefault(w => w.Kind == visual.Kind) ?? WidgetSettings.CreateDefault(visual.Kind)
                : null;
            var show = visual.Settings is { Enabled: true, DockDocked: true };
            visual.Host.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                visual.NextRefreshUtc = DateTime.MinValue;
            }
        }
        if (!enabled && _activePanelKind is not null)
        {
            CloseWidgetPanel();
        }
    }

    /// <summary>低帧率节流：各小组件按自身刷新频率更新展开面板内容，不做高频重绘。</summary>
    private void TickWidgets()
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var visual in _widgetVisuals.Values)
        {
            if (visual.Host.Visibility != Visibility.Visible || visual.Settings is null) continue;
            if (nowUtc < visual.NextRefreshUtc) continue;

            // 天气/音频为异步数据源，仅在到达刷新周期时触发更新（方法内部防重入）。
            if (visual.Kind == WidgetKind.Weather) _ = RefreshWeatherAsync();
            else if (visual.Kind == WidgetKind.AudioPlayer) _ = RefreshMediaSessionAsync();

            // 展示型小组件面板打开时同步更新内容
            if (_activePanelKind == visual.Kind && _activePanelDetail is not null)
                {
                    // 只改那一行文字：原先这里调 ShowWidgetPanel(force: true) 重建整棵面板，
                    // 每 500ms 一次、一小时约 7200 次元素创建，是内存与句柄增长的主因。
                    _activePanelDetail.Text = RenderWidgetDetail(visual.Kind);
                }
            visual.NextRefreshUtc = nowUtc.AddMilliseconds(Math.Max(WidgetSettings.MinimumRefreshMs, visual.Settings.RefreshIntervalMs));
        }
    }

    /// <summary>切换小组件内联面板：再次点击同一图标收起，点击其它图标切换内容。</summary>
    private void ToggleWidgetPanel(WidgetVisual visual)
    {
        if (_activePanelKind == visual.Kind)
        {
            CloseWidgetPanel();
            return;
        }
        ShowWidgetPanel(visual);
    }

    /// <summary>在内联面板区渲染指定小组件的交互内容。</summary>
    private void ShowWidgetPanel(WidgetVisual visual, bool force = false)
    {
        Border content;
        try
        {
            content = BuildWidgetPanel(visual);
        }
        catch (Exception)
        {
            // 构建面板内容失败（例如踩到 WPF 的元素复用约束）不该终结整个程序：
            // 音频面板曾因此在"关闭后再次打开"时闪退。这里降级为"没有打开"。
            _activePanelKind = null;
            return;
        }

        _activePanelKind = visual.Kind;

        // 面板改由独立弹窗承载：Dock 的尺寸全程不变，因此没有跳动，玻璃也不受扰动。
        if (_widgetPopup is { } open && open.IsVisible)
        {
            // 已经开着：切换小组件或每 500ms 的内容刷新——就地换内容，不重播动画。
            // 用户点了另一个图标时锚点也要跟着换，否则面板还停在旧图标上方（看着像"跑偏"）。
            open.SetContent(content);
            if (!force)
            {
                open.ReanchorTo(visual.Host);
            }

            return;
        }

        var popup = new WidgetPopupWindow();
        popup.SetContent(content);
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_widgetPopup, popup))
            {
                _widgetPopup = null;
            }
        };

        _widgetPopup = popup;
        // 弹窗自己负责"先备材质、再显示并展开"这套顺序（与启动台一致），
        // 定位所需的换算也由它用窗口自己的设备变换完成。
        popup.RevealAbove(visual.Host, animate: !force);
    }

    /// <summary>把面板高度从 0 动画到展开，让整个 Dock 向上延伸、下半部分保持不动。</summary>
    private void RevealWidgetPanelHeight(int token)
    {
        DockWidgetPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        DockWidgetPanel.MaxHeight = 0;

        // 新尺寸的玻璃要重出一帧；几何是逐帧变化的，捕获坐标也随之更新。
        GlassSurface.RequestBackdropRefresh(revealing: true);

        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            DockWidgetPanel.MaxHeight = WidgetPanelMaxHeightDip;
            return;
        }

        var grow = new DoubleAnimation(0, WidgetPanelMaxHeightDip, WidgetPanelRevealDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        grow.Completed += (_, _) =>
        {
            if (token != _widgetPanelReveal)
            {
                return;
            }

            // 交还自适应高度：留着固定值会让后续内容变高变矮时不再跟着变。
            DockWidgetPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            DockWidgetPanel.MaxHeight = WidgetPanelMaxHeightDip;
        };

        DockWidgetPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);
        RevealWidgetPanel(force: false);
    }

    /// <summary>
    /// 面板展开：淡入 + 由略小放大到原大小。
    /// </summary>
    /// <remarks>
    /// 原先只是把 Visibility 置为 Visible，面板"啪"地出现，与系统控制中心那种
    /// 柔和展开的手感差很远。
    /// <para>
    /// <paramref name="force"/> 是"内容重绘但不该重播动画"（每 500ms 的刷新路径走它），
    /// 那时保持当前状态不动；关闭动画时也不重播。
    /// </para>
    /// </remarks>
    private void RevealWidgetPanel(bool force)
    {
        // 递增即作废上一次"收起"的收尾动作：收起动画还没播完就又点开时，
        // 那次的 Completed 不能把刚展开的面板收掉。
        ++_widgetPanelReveal;

        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            SettleWidgetPanel();
            return;
        }

        if (force)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        DockWidgetPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, WidgetPanelRevealDuration)
        {
            EasingFunction = ease,
        });

        // 轻微回弹让"长出来"的感觉更接近控制中心；幅度刻意很小，大了会看到边缘抖动。
        var spring = new BackEase { Amplitude = .12, EasingMode = EasingMode.EaseOut };
        _widgetPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(WidgetPanelRestScale, 1, WidgetPanelRevealDuration) { EasingFunction = spring });
        _widgetPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(WidgetPanelRestScale, 1, WidgetPanelRevealDuration) { EasingFunction = spring });
    }

    /// <summary>把面板立即置于"完全展开、无动画"的状态。</summary>
    private void SettleWidgetPanel()
    {
        DockWidgetPanel.BeginAnimation(UIElement.OpacityProperty, null);
        DockWidgetPanel.Opacity = 1;
        _widgetPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _widgetPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _widgetPanelScale.ScaleX = _widgetPanelScale.ScaleY = 1;
    }

    private void CloseWidgetPanel()
    {
        _activePanelKind = null;
        _editingNoteId = null;
        _activePanelDetail = null;

        // 弹窗负责收起动画与关窗；Dock 里的内联面板从未显示过，
        // 所以下面的分支会立刻走到收尾（不播动画）。
        _widgetPopup?.CloseAnimated();
        _widgetPopup = null;

        if (ThemeService.Current?.AnimationsEnabled == false || DockWidgetPanel.Visibility != Visibility.Visible)
        {
            FinishCloseWidgetPanel();
            return;
        }

        // 收起：高度收回去（Dock 随之向下缩短），卡片同时淡出。
        // 收尾挂在**高度动画**的 Completed 上——它才是几何结束的时刻；
        // token 用来作废这一次收尾：期间用户又点开别的面板时，不能把新面板收掉。
        var token = ++_widgetPanelReveal;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var shrink = new DoubleAnimation(DockWidgetPanel.MaxHeight, 0, WidgetPanelHideDuration)
        {
            EasingFunction = ease,
        };
        shrink.Completed += (_, _) =>
        {
            if (token == _widgetPanelReveal)
            {
                FinishCloseWidgetPanel();
            }
        };

        DockWidgetPanel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, WidgetPanelHideDuration) { EasingFunction = ease });
        _widgetPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, WidgetPanelRestScale, WidgetPanelHideDuration) { EasingFunction = ease });
        _widgetPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, WidgetPanelRestScale, WidgetPanelHideDuration) { EasingFunction = ease });
        DockWidgetPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
    }

    private void FinishCloseWidgetPanel()
    {
        DockWidgetPanel.Content = null;
        DockWidgetPanel.Visibility = Visibility.Collapsed;
        // 交还 XAML 里的上限，下一次展开从同一个起点开始。
        DockWidgetPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        DockWidgetPanel.MaxHeight = WidgetPanelMaxHeightDip;
        SettleWidgetPanel();
    }

    /// <summary>构建小组件内联面板 UI（iOS 控制中心风格：大圆角半透明毛玻璃卡片）。</summary>
    private Border BuildWidgetPanel(WidgetVisual visual)
    {
        var title = new TextBlock
        {
            Text = GetWidgetIdentity(visual.Kind).Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 204, 215)),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ApplyRoundedChrome(closeBtn, 12);
        closeBtn.Click += (_, _) => CloseWidgetPanel();

        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(closeBtn, System.Windows.Controls.Dock.Right);
        titleRow.Children.Add(closeBtn);
        titleRow.Children.Add(title);

        UIElement body = visual.Kind switch
        {
            WidgetKind.Clock or WidgetKind.Date or WidgetKind.SystemMonitor
                => BuildSimpleBody(visual.Kind),
            WidgetKind.Weather => BuildWeatherBody(),
            WidgetKind.AudioPlayer => BuildAudioBody(visual),
            WidgetKind.Notes => BuildNotesBody(),
            WidgetKind.Todo => BuildTodoBody(),
            _ => BuildSimpleBody(visual.Kind),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(titleRow);
        stack.Children.Add(body);

        // iOS 玻璃卡片：浅色半透明底 + 高光边框
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 10, 0, 0),
            MaxWidth = 440,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        return border;
    }

    private TextBlock BuildSimpleBody(WidgetKind kind)
    {
        var text = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            // 不设 LineHeight：22 号中文的行框本身就比 30 高一点，
            // 钉死成 30 会把「2026年10月17日 星期六」这类文字的下缘裁掉。
            Text = RenderWidgetDetail(kind),
        };
        _activePanelDetail = text;
        return text;
    }

    private StackPanel BuildWeatherBody()
    {
        var info = new TextBlock
        {
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 228, 235)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Text = RenderWeatherDetail(),
        };

        // 输入框与"手动定位"按钮是同一组控件，规格必须对齐：
        // 同高 30、同字号 12、同圆角 8、各自留出内边距。原先输入框是 180 宽 + 字号 13 + 直角，
        // 放在一个字号 12、圆角 8 的按钮旁边，既宽窄不称、圆角与字号也对不上。
        // TextBox 自身画不出圆角，所以外面套一层 PhoneBorder（与按钮模板同一个控件），内层透明无边框。
        // 交给主题：主题里本来就有 TextBox 的隐式样式（圆角 12、半透明白底、聚焦变蓝、
        // 文字色随明暗主题）。之前这里自行覆盖底色/边框/字号，等于又造了第三套样式，
        // 浅色主题下会显示不对。现在只保留布局属性。
        var cityInput = new TextBox
        {
            Width = 140,
            Height = 30,
            // 显式定 12 并收紧内边距：主题默认 Padding 是 9,5，30 高的框只剩 20，
            // 中文行框比这更高，字会被上下裁掉。
            FontSize = 12,
            Padding = new Thickness(9, 3, 9, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "输入城市名，例如：广州",
            Margin = new Thickness(0, 0, 8, 0),
        };
        var locateBtn = new Button
        {
            Content = "手动定位",
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(255, 64, 128, 255)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        ApplyRoundedChrome(locateBtn, 8);
        locateBtn.Click += async (_, _) =>
        {
            var city = cityInput.Text.Trim();
            if (city.Length == 0) return;
            locateBtn.IsEnabled = false;
            info.Text = "正在定位 " + city + " ...";
            await SetWeatherCityAsync(city);
            info.Text = RenderWeatherDetail();
            locateBtn.IsEnabled = true;
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };
        row.Children.Add(cityInput);
        row.Children.Add(locateBtn);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(info);
        stack.Children.Add(row);
        return stack;
    }

    private StackPanel BuildAudioBody(WidgetVisual visual)
    {
        var info = new TextBlock
        {
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 228, 235)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Text = RenderAudioDetail(),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        // 每次重建面板都新建这批按钮。
        //
        // 这里**绝不能**复用 visual.MediaControls 里的实例：关闭面板只是把内容树摘下，
        // 按钮的逻辑父级仍指向旧的行容器；第二次 Add 到新父级会抛
        // InvalidOperationException（元素已是另一个元素的逻辑子级）。
        // 而这个异常是在小组件图标的同步 Click 处理器里抛出的，会直接终结进程——
        // 这就是"打开音频面板 → 关闭 → 再打开就闪退"的原因。
        var mc = new MediaControlButtons
        {
            Prev = new Button { Content = "⏮" },
            Play = new Button { Content = "▶" },
            Next = new Button { Content = "⏭" },
        };
        // async void 的事件处理器里只要逃出一个异常就会崩掉整个进程，
        // 而媒体会话随时可能失效，所以统一走带兜底的入口。
        mc.Prev.Click += async (_, _) => await RunMediaCommandAsync(MediaPreviousAsync);
        mc.Play.Click += async (_, _) => await RunMediaCommandAsync(MediaPlayPauseAsync);
        mc.Next.Click += async (_, _) => await RunMediaCommandAsync(MediaNextAsync);
        visual.MediaControls = mc;

        // 圆形毛玻璃控制按钮：播放键蓝色高亮，与 iOS 控制中心一致
        mc.Prev.Width = 40; mc.Prev.Height = 40; mc.Prev.FontSize = 16;
        mc.Prev.Padding = new Thickness(0);
        mc.Prev.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        mc.Prev.BorderThickness = new Thickness(0);
        mc.Prev.Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 224, 235));
        ApplyRoundedChrome(mc.Prev, 20);
        mc.Prev.Margin = new Thickness(0, 0, 14, 0);
        row.Children.Add(mc.Prev);

        mc.Play.Width = 52; mc.Play.Height = 52; mc.Play.FontSize = 24;
        mc.Play.Padding = new Thickness(0);
        mc.Play.BorderThickness = new Thickness(0);
        ApplyRoundedChrome(mc.Play, 26);
        mc.Play.Margin = new Thickness(0, 0, 14, 0);
        mc.Play.Foreground = Brushes.White;
        mc.Play.Background = _media.HasSession
            ? new SolidColorBrush(Color.FromArgb(255, 64, 128, 255))
            : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        row.Children.Add(mc.Play);

        mc.Next.Width = 40; mc.Next.Height = 40; mc.Next.FontSize = 16;
        mc.Next.Padding = new Thickness(0);
        mc.Next.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        mc.Next.BorderThickness = new Thickness(0);
        mc.Next.Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 224, 235));
        ApplyRoundedChrome(mc.Next, 20);
        row.Children.Add(mc.Next);

        // 音量：滑杆（系统主音量，走 Core Audio）+ 静音按钮。
        // 静音沿用系统音量键（零新增互操作）；滑杆必须能读回当前音量，所以只能用 COM。
        var muteBtn = new Button
        {
            Content = "🔇",
            Width = 32,
            Height = 32,
            FontSize = 13,
            Padding = new Thickness(0),
            Margin = new Thickness(12, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 224, 235)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "静音 / 取消静音",
        };
        ApplyRoundedChrome(muteBtn, 16);
        muteBtn.Click += (_, _) => TapVolumeKey(VolumeMuteKey);

        var volumeSlider = new Slider
        {
            Width = 132,
            Minimum = 0,
            Maximum = 100,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "系统音量",
            IsMoveToPointEnabled = true,
        };
        if (TryGetMasterVolume(out var currentVolume))
        {
            // 只在读得到的时候设初值；读不到就停在 0，避免显示一个假音量。
            volumeSlider.Value = Math.Round(currentVolume * 100);
        }

        // 拖动时写入系统音量；程序化设值（上面那一步）不写回。
        var applyingVolume = false;
        volumeSlider.ValueChanged += (_, e) =>
        {
            if (applyingVolume)
            {
                return;
            }

            applyingVolume = true;
            try
            {
                TrySetMasterVolume((float)(e.NewValue / 100d));
            }
            finally
            {
                applyingVolume = false;
            }
        };

        row.Children.Add(muteBtn);
        row.Children.Add(volumeSlider);

        // 新按钮的可用状态按当前快照对齐（原来靠创建时的 IsEnabled = false 兜着）。
        UpdateMediaButtons();

        // 专辑封面与专辑名：封面来自 SMTC（见 RefreshMediaSessionAsync，按曲目缓存，
        // 只有切歌时才重新解码）。没有封面/专辑时这两块都不加，布局与原来完全一致。
        var textColumn = new StackPanel { Orientation = Orientation.Vertical };
        textColumn.Children.Add(info);
        if (!string.IsNullOrWhiteSpace(_media.Album))
        {
            textColumn.Children.Add(new TextBlock
            {
                Text = _media.Album,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 168, 172, 184)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        var head = new DockPanel();
        if (_media.Cover is { } cover)
        {
            // 圆角用 Clip 实现：UniformToFill 会稍微溢出 72×72 的框，正好被圆角裁掉。
            var artwork = new Image
            {
                Width = 72,
                Height = 72,
                Stretch = Stretch.UniformToFill,
                Source = cover,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Clip = new RectangleGeometry(new Rect(0, 0, 72, 72), 10, 10),
            };
            DockPanel.SetDock(artwork, System.Windows.Controls.Dock.Left);
            head.Children.Add(artwork);
        }

        head.Children.Add(textColumn);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(head);
        stack.Children.Add(row);
        return stack;
    }

    private StackPanel BuildNotesBody()
    {
        var list = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var note in _notes.OrderByDescending(n => n.CreatedAt))
        {
            var item = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4),
            };
            var itemText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(note.Title) ? "(无标题)" : note.Title,
                FontSize = 13,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (!string.IsNullOrWhiteSpace(note.Content))
            {
                var contentText = new TextBlock
                {
                    Text = note.Content,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 184, 195)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                itemText = new TextBlock { };
                var v = new StackPanel { Orientation = Orientation.Vertical };
                // 标题也可能很长：不换行就会被卡片右边界直接裁断。
                v.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(note.Title) ? "(无标题)" : note.Title,
                    FontSize = 13,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                });
                v.Children.Add(contentText);
                itemText = null!;
                item.Child = v;
            }
            else
            {
                item.Child = itemText;
            }

            var editBtn = new Button { Content = "编辑", FontSize = 11, Height = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(0), Foreground = Brushes.White };
            ApplyRoundedChrome(editBtn, 8);
            var delBtn = new Button { Content = "删除", FontSize = 11, Height = 26, Padding = new Thickness(10, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(120, 220, 80, 90)), BorderThickness = new Thickness(0), Foreground = Brushes.White };
            ApplyRoundedChrome(delBtn, 8);
            editBtn.Click += (_, _) =>
            {
                _editingNoteId = note.Id;
                RefreshNotesPanel();
            };
            delBtn.Click += (_, _) =>
            {
                DeleteNote(note.Id);
                RefreshNotesPanel();
            };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            btns.Children.Add(editBtn);
            btns.Children.Add(delBtn);

            var row = new DockPanel();
            DockPanel.SetDock(btns, System.Windows.Controls.Dock.Right);
            row.Children.Add(btns);
            if (item.Child is StackPanel sv)
            {
                row.Children.Add(sv);
            }
            else
            {
                row.Children.Add(itemText ?? new TextBlock());
            }
            item.Child = row;
            list.Children.Add(item);
        }

        // 编辑区改为**纵向**排布。
        //
        // 原先标题、内容、按钮挤在同一个横向行里：标签≈40 + 输入框 220 + 标签≈45 + 输入框 220 + 按钮≈80
        // ≈ 605 DIP，而卡片宽度上限是 440，横向 StackPanel 又不会换行——右侧控件必然被裁掉。
        //
        // 同时把字号/圆角/内边距与卡片里其它输入框统一：字号 12、圆角 8（PhoneBorder 外壳）、
        // 文字内缩 8，标题框高度对齐按钮的 30。
        // 交给主题（隐式 TextBox 样式负责圆角、底色、聚焦高亮与明暗跟随），只留布局属性。
        var titleInput = new TextBox
        {
            Width = 260,
            Height = 30,
            FontSize = 12,
            Padding = new Thickness(9, 3, 9, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "标题",
        };
        var contentInput = new TextBox
        {
            Width = 260,
            Height = 64,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            AcceptsReturn = true,
            // 固定高度的多行框：内容超过两行时必须能滚动，否则后面的文字被直接裁掉。
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "内容（可空）",
        };
        var saveBtn = new Button { Content = "保存", Height = 30, Padding = new Thickness(14, 0, 14, 0), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(255, 64, 128, 255)), BorderThickness = new Thickness(0), Foreground = Brushes.White };
        ApplyRoundedChrome(saveBtn, 8);
        saveBtn.Click += (_, _) =>
        {
            if (_editingNoteId is { } id)
            {
                EditNote(id, titleInput.Text.Trim(), contentInput.Text.Trim());
                _editingNoteId = null;
            }
            else
            {
                AddNote(titleInput.Text.Trim(), contentInput.Text.Trim());
            }
            titleInput.Clear();
            contentInput.Clear();
            RefreshNotesPanel();
        };

        var saveRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        saveRow.Children.Add(saveBtn);

        var editRow = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 6) };
        editRow.Children.Add(new TextBlock { Text = "标题", FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
        editRow.Children.Add(titleInput);
        editRow.Children.Add(new TextBlock { Text = "内容", FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 4) });
        editRow.Children.Add(contentInput);
        editRow.Children.Add(saveRow);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(list);
        stack.Children.Add(editRow);
        return stack;
    }

    private void RefreshNotesPanel()
    {
        if (_activePanelKind == WidgetKind.Notes && _widgetVisuals.TryGetValue(WidgetKind.Notes, out var visual))
        {
            ShowWidgetPanel(visual);
        }
    }

    private StackPanel BuildTodoBody()
    {
        var list = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var todo in _todos.OrderBy(t => t.Done).ThenByDescending(t => t.CreatedAt))
        {
            var check = new CheckBox
            {
                IsChecked = todo.Done,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "点击切换完成状态",
            };
            check.Checked += (_, _) => ToggleTodo(todo.Id);
            check.Unchecked += (_, _) => ToggleTodo(todo.Id);

            var delBtn = new Button { Content = "✕", FontSize = 11, Width = 22, Height = 22, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 154, 165)), Cursor = System.Windows.Input.Cursors.Hand };
            delBtn.Click += (_, _) =>
            {
                DeleteTodo(todo.Id);
                RefreshTodoPanel();
            };

            var todoText = new TextBlock
            {
                Text = todo.Text,
                FontSize = 13,
                Foreground = todo.Done ? new SolidColorBrush(Color.FromArgb(255, 130, 134, 145)) : Brushes.White,
                TextDecorations = todo.Done ? TextDecorations.Strikethrough : null,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(delBtn, System.Windows.Controls.Dock.Right);
            row.Children.Add(delBtn);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(check);
            left.Children.Add(todoText);
            row.Children.Add(left);
            list.Children.Add(row);
        }

        // 与天气/备忘统一规格：字号 12、圆角 8（PhoneBorder 外壳，和按钮模板同一个控件）、
        // 文字内缩 8；高度 30 与"添加"按钮一致。宽度从 220 收到 200，与按钮的比例更协调。
        // TextBox 自身画不出圆角，所以外面套一层，内层透明无边框。
        var input = new TextBox
        {
            Width = 200,
            Height = 30,
            FontSize = 12,
            Padding = new Thickness(9, 3, 9, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "新待办内容",
            Margin = new Thickness(0, 0, 8, 0),
        };
        var addBtn = new Button { Content = "添加", Height = 30, Padding = new Thickness(14, 0, 14, 0), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(255, 64, 128, 255)), BorderThickness = new Thickness(0), Foreground = Brushes.White };
        ApplyRoundedChrome(addBtn, 8);
        addBtn.Click += (_, _) =>
        {
            if (input.Text.Trim().Length == 0) return;
            AddTodo(input.Text.Trim());
            input.Clear();
            RefreshTodoPanel();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(input);
        addRow.Children.Add(addBtn);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(list);
        stack.Children.Add(addRow);
        return stack;
    }

    private void RefreshTodoPanel()
    {
        if (_activePanelKind == WidgetKind.Todo && _widgetVisuals.TryGetValue(WidgetKind.Todo, out var visual))
        {
            ShowWidgetPanel(visual);
        }
    }

    // 备忘录和待办增删改查功能
    private static void AddNote(string title, string content)
    {
        var note = new NoteItem
        {
            Id = _notes.Count > 0 ? _notes.Max(n => n.Id) + 1 : 1,
            Title = title,
            Content = content,
            CreatedAt = DateTime.Now
        };
        _notes.Add(note);
        SaveNotes();
    }

    private static void EditNote(int id, string title, string content)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note != null)
        {
            note.Title = title;
            note.Content = content;
            SaveNotes();
        }
    }

    private static void DeleteNote(int id)
    {
        _notes.RemoveAll(n => n.Id == id);
        SaveNotes();
    }

    private static void AddTodo(string text)
    {
        var todo = new TodoItem
        {
            Id = _todos.Count > 0 ? _todos.Max(t => t.Id) + 1 : 1,
            Text = text,
            Done = false,
            CreatedAt = DateTime.Now
        };
        _todos.Add(todo);
        SaveTodos();
    }

    private static void ToggleTodo(int id)
    {
        var todo = _todos.FirstOrDefault(t => t.Id == id);
        if (todo != null)
        {
            todo.Done = !todo.Done;
            SaveTodos();
        }
    }

    private static void DeleteTodo(int id)
    {
        _todos.RemoveAll(t => t.Id == id);
        SaveTodos();
    }

    /// <summary>保存备忘录数据到本地 JSON 文件</summary>
    private static void SaveNotes()
    {
        try
        {
            var json = JsonSerializer.Serialize(_notes, NotesJsonOptions);
            File.WriteAllText(NotesPath, json);
        }
        catch (Exception)
        {
            // 静默失败
        }
    }

    /// <summary>保存待办事项数据到本地 JSON 文件</summary>
    private static void SaveTodos()
    {
        try
        {
            var json = JsonSerializer.Serialize(_todos, NotesJsonOptions);
            File.WriteAllText(TodoPath, json);
        }
        catch (Exception)
        {
            // 静默失败
        }
    }

    /// <summary>从本地 JSON 文件加载备忘录数据</summary>
    private static async Task LoadNotes()
    {
        try
        {
            if (File.Exists(NotesPath))
            {
                var json = await File.ReadAllTextAsync(NotesPath);
                _notes = JsonSerializer.Deserialize<List<NoteItem>>(json) ?? new List<NoteItem>();
            }
        }
        catch (Exception)
        {
            _notes = new List<NoteItem>();
        }
    }

    /// <summary>从本地 JSON 文件加载待办事项数据</summary>
    private static async Task LoadTodos()
    {
        try
        {
            if (File.Exists(TodoPath))
            {
                var json = await File.ReadAllTextAsync(TodoPath);
                _todos = JsonSerializer.Deserialize<List<TodoItem>>(json) ?? new List<TodoItem>();
            }
        }
        catch (Exception)
        {
            _todos = new List<TodoItem>();
        }
    }

    /// <summary>加载数据（备忘录和待办）</summary>
    private static async Task LoadDataAsync()
    {
        await LoadNotes();
        await LoadTodos();
    }

    /// <summary>
    /// 按 Dock 固定入口的规格生成小组件图标。
    /// </summary>
    /// <remarks>
    /// 固定入口（启动台、此电脑、切换桌面图标、设置）用的是同一套写法：48×48 的
    /// Viewbox 套 Canvas，里面是描边路径——无填充、线宽 3.5、圆角接头与端帽，
    /// 颜色绑到窗口的 ForegroundBrush 并随主题变化。
    /// 小组件图标原先用彩色 emoji（Segoe UI Emoji）加下方 9pt 文字标题，
    /// 既不单色、尺寸与结构也与旁边那排不一致；这里改成同一套矢量写法。
    /// </remarks>
    private Viewbox BuildWidgetIcon(string iconData)
    {
        // 限定名：本文件同时用 System.IO.Path 做文件操作，直接 using Shapes 会让 Path 变成歧义引用。
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconData),
            Fill = Brushes.Transparent,
            StrokeThickness = 3.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        // 与 XAML 里的入口图标一样绑到窗口前景色：换主题时自动跟着变，
        // 不需要在 RefreshWidgets 里逐项涂色。
        path.SetBinding(
            System.Windows.Shapes.Shape.StrokeProperty,
            new Binding(nameof(ForegroundBrush)) { Source = this });

        var canvas = new Canvas { Width = 48, Height = 48 };
        canvas.Children.Add(path);

        return new Viewbox
        {
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
    }

    /// <summary>
    /// 小组件图标的矢量路径与名称。
    /// </summary>
    /// <remarks>
    /// 路径画在 48×48 画布上、图形收在 3–45 之间，与固定入口的图标同一尺度与笔画。
    /// 八种图标各自可辨：时钟是圆盘加指针、日期是单日卡片、日历是带挂环的月网格、
    /// 监控是仪表盘、天气是云加雨、音频是音符、备忘是带折角的纸、待办是方框加对勾。
    /// </remarks>
    private static (string IconData, string Name) GetWidgetIdentity(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => ("M24,5 A19,19 0 1,1 23.99,5 M24,13 V25 L32,30", "时钟"),
        WidgetKind.Date => ("M9,10 H39 Q42,10 42,13 V39 Q42,42 39,42 H9 Q6,42 6,39 V13 Q6,10 9,10 Z M6,19 H42 M24,26 V36", "日期"),
        WidgetKind.SystemMonitor => ("M8,34 A16,16 0 1,1 40,34 M24,34 L33,23 M12,23 L15,26 M24,18 V22 M36,23 L33,26", "监控"),
        WidgetKind.Weather => ("M13,30 H35 Q39,30 39,26 Q39,22 35,22 Q34,15 27,15 Q20,15 19,22 Q13,22 13,26 Q13,30 13,30 Z M18,35 L16,41 M26,35 L24,41 M34,35 L32,41", "天气"),
        WidgetKind.AudioPlayer => ("M22,36 A4.5,4.5 0 1,1 21.99,36 M26,33 V10 Q35,13 35,23", "音频"),
        WidgetKind.Notes => ("M14,6 H30 L40,16 V40 Q40,42 38,42 H14 Q12,42 12,40 V8 Q12,6 14,6 Z M30,6 V16 H40 M19,26 H33 M19,33 H33", "备忘"),
        WidgetKind.Todo => ("M12,8 H36 Q40,8 40,12 V36 Q40,40 36,40 H12 Q8,40 8,36 V12 Q8,8 12,8 Z M16,24 L21,29 L32,17", "待办"),
        _ => ("M12,12 H36 Q40,12 40,16 V32 Q40,36 36,36 H12 Q8,36 8,32 V16 Q8,12 12,12 Z M24,22 A2,2 0 1,1 23.99,22", "小组件"),
    };

    private string RenderWidgetDetail(WidgetKind kind)
    {
        var now = DateTime.Now;
        return kind switch
        {
            WidgetKind.Clock => $"{now:HH:mm:ss}\n{now.ToString("yyyy年M月d日 dddd", WidgetCulture)}",
            WidgetKind.Date => now.ToString("yyyy年M月d日 dddd", WidgetCulture),
            WidgetKind.SystemMonitor => $"CPU 占用 {SampleCpuPercent():0}%\n内存占用 {SampleMemoryPercent():0}%",
            WidgetKind.Weather => RenderWeatherDetail(),
            WidgetKind.AudioPlayer => RenderAudioDetail(),
            WidgetKind.Notes => RenderNotesDetail(),
            WidgetKind.Todo => RenderTodoDetail(),
            _ => string.Empty,
        };
    }

    private string RenderWeatherDetail()
    {
        var w = _weather;
        if (w.Ready && !w.Offline)
        {
            var where = string.IsNullOrWhiteSpace(w.Location) ? "当前位置" : w.Location;
            return $"{where} {w.TemperatureC:0.0}°C {w.Condition}\n更新于 {w.FetchedUtc.ToLocalTime():HH:mm}";
        }
        return "天气 --°C（离线）\n网络不可用或定位失败，可输入城市手动定位";
    }

    private string RenderAudioDetail()
    {
        var s = _media;
        if (!s.HasSession)
        {
            return "当前无播放\n暂无活动的媒体会话";
        }
        var stateText = s.State switch
        {
            MediaPlayerState.Playing => "播放中",
            MediaPlayerState.Paused => "已暂停",
            _ => "未知状态",
        };
        var title = string.IsNullOrWhiteSpace(s.Title) ? "未知曲目" : s.Title;
        var artist = string.IsNullOrWhiteSpace(s.Artist) ? "未知艺术家" : s.Artist;
        return $"{title}\n{artist}\n{stateText}";
    }

    private static string RenderNotesDetail()
    {
        if (_notes.Count == 0)
        {
            return "备忘录\n暂无内容";
        }
        var lines = new List<string> { $"备忘录 ({_notes.Count} 条)" };
        for (int i = 0; i < Math.Min(_notes.Count, 5); i++)
        {
            var note = _notes[i];
            lines.Add($"[{i + 1}] {note.Title}");
        }
        return string.Join('\n', lines);
    }

    private static string RenderTodoDetail()
    {
        var activeCount = _todos.Count(t => !t.Done);
        var totalCount = _todos.Count;
        if (totalCount == 0)
        {
            return "待办事项\n暂无任务";
        }
        var lines = new List<string> { $"待办事项 ({activeCount}/{totalCount})" };
        for (int i = 0; i < Math.Min(totalCount, 10); i++)
        {
            var todo = _todos[i];
            lines.Add($"{(todo.Done ? "☑" : "☐")} {todo.Text}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>异步拉取天气：IP 定位（一次）→ Open-Meteo 实时数据；失败/无网络时降级离线文案，绝不抛异常。</summary>
    private async Task RefreshWeatherAsync()
    {
        if (_weatherFetching) return;
        _weatherFetching = true;
        try
        {
            if (!_weatherLocateStarted)
            {
                _weatherLocateStarted = true;
                // 设置里存了城市就优先用它（重启后自动填写）；留空或解析失败才按 IP 自动定位。
                var savedCity = WidgetSettingsHost.Current?.Items
                    .FirstOrDefault(w => w.Kind == WidgetKind.Weather)?.WeatherLocation ?? "";
                if (string.IsNullOrWhiteSpace(savedCity) || !await TryLocateWithPhotonAsync(savedCity))
                {
                    await LocateCityAsync();
                }
            }
            if (_weatherLat == 0 && _weatherLon == 0)
            {
                _weather = new WeatherSnapshot { Offline = true, Location = _weatherLocation, FetchedUtc = DateTime.UtcNow };
                return;
            }
            var url = "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={_weatherLat.ToString("R", CultureInfo.InvariantCulture)}"
                + $"&longitude={_weatherLon.ToString("R", CultureInfo.InvariantCulture)}"
                + "&current_weather=true&temperature_unit=celsius";
            using var response = await WeatherHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double? temp = null;
            string? code = null;
            if (root.TryGetProperty("current_weather", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    temp = t.GetDouble();
                }
                if (current.TryGetProperty("weathercode", out var c) && c.ValueKind == JsonValueKind.Number)
                {
                    code = c.GetInt32().ToString(CultureInfo.InvariantCulture);
                }
            }
            if (temp is null)
            {
                _weather = new WeatherSnapshot { Offline = true, Location = _weatherLocation, FetchedUtc = DateTime.UtcNow };
                return;
            }
            _weather = new WeatherSnapshot
            {
                Ready = true,
                Offline = false,
                TemperatureC = temp.Value,
                Condition = DescribeWeatherCode(code),
                Location = _weatherLocation,
                FetchedUtc = DateTime.UtcNow,
            };
        }
        catch (Exception)
        {
            _weather = new WeatherSnapshot { Offline = true, Location = _weatherLocation, FetchedUtc = DateTime.UtcNow };
        }
        finally
        {
            _weatherFetching = false;
        }
    }

    private async Task LocateCityAsync()
    {
        try
        {
            using var response = await WeatherHttp.GetAsync(
                "http://ip-api.com/json/?fields=status,lat,lon,city,regionName",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() == "success"
                && root.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number
                && root.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number)
            {
                _weatherLat = lat.GetDouble();
                _weatherLon = lon.GetDouble();
                var city = root.TryGetProperty("city", out var c) ? c.GetString() : "";
                var region = root.TryGetProperty("regionName", out var r) ? r.GetString() : "";
                _weatherLocation = string.IsNullOrWhiteSpace(city) ? "本地" : city;
                if (!string.IsNullOrWhiteSpace(region) && !string.Equals(region, city, StringComparison.Ordinal))
                {
                    _weatherLocation += $" {region}";
                }
            }
            else
            {
                _weatherLocation = "";
            }
        }
        catch (Exception)
        {
            _weatherLocation = "";
        }
    }

    /// <summary>手动选择天气城市（nominatim 地理编码）。</summary>
    private async Task SetWeatherCityAsync(string city)
    {
        try
        {
            // 首选 Photon（实测能解析到"区"），失败再退回下面的 Open-Meteo 地理编码——两者覆盖面互补。
            if (await TryLocateWithPhotonAsync(city))
            {
                return;
            }

            // 用 Open-Meteo 的地理编码，而不是 nominatim：
            // 天气数据本来就取自 api.open-meteo.com（在这台机器上可用），
            // 而 nominatim.openstreetmap.org 这里直连超时——换了城市也永远等不到结果。
            //
            // **取 10 条候选再自己挑**，不能像原先那样 count=1 只拿第一条：
            // 数据源把同名城市排在区的前面，输入"天河区"时会被同名城市抢走；
            // 而且区级名称落在 admin2/admin3/admin4 上，只读 name/admin1 是看不到的。
            using var response = await WeatherHttp.GetAsync(
                "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city) + "&count=10&language=zh&format=json",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
            {
                static bool Hit(JsonElement candidate, string field, string token) =>
                    candidate.TryGetProperty(field, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text
                    && text.Contains(token, StringComparison.Ordinal);

                // 把输入按空格/逗号切开逐段比对：命中 name 记 3 分（说明就是这个地方本身），
                // 命中某一级行政区记 1 分。得分最高者胜出，同分保持接口返回的顺序（即相关度）。
                var tokens = city.Split([' ', ',', '，', '　'], StringSplitOptions.RemoveEmptyEntries);
                var best = results[0];
                var bestScore = -1;
                foreach (var candidate in results.EnumerateArray())
                {
                    var score = 0;
                    foreach (var token in tokens)
                    {
                        if (Hit(candidate, "name", token)) score += 3;
                        else if (Hit(candidate, "admin1", token) || Hit(candidate, "admin2", token)
                            || Hit(candidate, "admin3", token) || Hit(candidate, "admin4", token)) score += 1;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }

                if (best.TryGetProperty("latitude", out var lat) && lat.ValueKind == JsonValueKind.Number
                    && best.TryGetProperty("longitude", out var lon) && lon.ValueKind == JsonValueKind.Number)
                {
                    _weatherLat = lat.GetDouble();
                    _weatherLon = lon.GetDouble();
                    var name = best.TryGetProperty("name", out var n) ? n.GetString() : "";
                    // 标签优先带市一级（用户更熟悉），没有市就退回省。
                    var city2 = best.TryGetProperty("admin2", out var a2) ? a2.GetString() : "";
                    var province = best.TryGetProperty("admin1", out var a1) ? a1.GetString() : "";
                    var label = string.IsNullOrWhiteSpace(name) ? city : name;
                    var qualifier = !string.IsNullOrWhiteSpace(city2) ? city2 : province;
                    if (!string.IsNullOrWhiteSpace(qualifier) && !string.Equals(qualifier, label, StringComparison.Ordinal))
                    {
                        label += $" {qualifier}";
                    }

                    _weatherLocation = label;
                    _weather = new WeatherSnapshot { Ready = false, Offline = false, Location = _weatherLocation, FetchedUtc = DateTime.UtcNow };
                    _weatherLocateStarted = true;
                    await RefreshWeatherAsync();
                    return;
                }
            }
            _weather = new WeatherSnapshot { Offline = true, Location = city, FetchedUtc = DateTime.UtcNow };
        }
        catch (Exception)
        {
            _weather = new WeatherSnapshot { Offline = true, Location = city, FetchedUtc = DateTime.UtcNow };
        }
    }

    private static string DescribeWeatherCode(string? code) => code switch
    {
        "0" => "晴",
        "1" => "大部晴朗",
        "2" => "多云",
        "3" => "阴",
        "45" or "48" => "雾",
        "51" or "53" or "55" => "毛毛雨",
        "56" or "57" => "冻雨",
        "61" or "63" or "65" => "雨",
        "66" or "67" => "冻雨",
        "71" or "73" or "75" => "雪",
        "77" => "雪粒",
        "80" or "81" or "82" => "阵雨",
        "85" or "86" => "阵雪",
        "95" => "雷暴",
        "96" or "99" => "强雷暴",
        _ => "未知",
    };

    private void UpdateMediaButtons()
    {
        if (_windowClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            // 调度器已关闭时 BeginInvoke 会抛，而本方法是从 finally 与定时器路径调用的，
            // 一旦逃出去就是进程崩溃。
            try
            {
                Dispatcher.BeginInvoke(UpdateMediaButtons);
            }
            catch (Exception)
            {
            }

            return;
        }
        foreach (var visual in _widgetVisuals.Values)
        {
            if (visual.Kind != WidgetKind.AudioPlayer || visual.MediaControls is null) continue;
            var enabled = _media.HasSession;
            visual.MediaControls.Prev.IsEnabled = enabled;
            visual.MediaControls.Next.IsEnabled = enabled;
            visual.MediaControls.Play.IsEnabled = enabled;
            visual.MediaControls.Play.Content = _media.State == MediaPlayerState.Playing ? "⏸" : "▶";
            visual.MediaControls.Play.Background = enabled
                ? new SolidColorBrush(Color.FromArgb(255, 64, 128, 255))
                : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            visual.MediaControls.Play.Foreground = enabled
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(255, 220, 224, 235));
        }
    }

    /// <summary>收起/空闲（性能休眠）或不可见时暂停小组件定时器，避免后台空转。</summary>
    private void UpdateWidgetTimerState()
    {
        var shouldRun = IsVisible && !IsPerformanceSleeping
            && (WidgetSettingsHost.Current?.Enabled ?? false)
            && _widgetVisuals.Values.Any(v => v.Host.Visibility == Visibility.Visible);
        if (shouldRun)
        {
            if (!_widgetsTimer.IsEnabled) _widgetsTimer.Start();
        }
        else
        {
            _widgetsTimer.Stop();
        }
    }

    private double SampleCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return double.NaN;
        }
        if (_lastCpuSampleTime == default)
        {
            _lastCpuIdle = idle; _lastCpuKernel = kernel; _lastCpuUser = user;
            _lastCpuSampleTime = DateTime.UtcNow;
            return double.NaN;
        }
        var idleDelta = idle - _lastCpuIdle;
        var kernelDelta = kernel - _lastCpuKernel;
        var userDelta = user - _lastCpuUser;
        _lastCpuIdle = idle; _lastCpuKernel = kernel; _lastCpuUser = user;
        _lastCpuSampleTime = DateTime.UtcNow;
        var total = kernelDelta + userDelta;
        if (total <= 0) return 0;
        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    private static double SampleMemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return double.NaN;
        return (status.TotalPhys - status.AvailPhys) * 100.0 / status.TotalPhys;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong Extend;
    }
}

