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
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private static readonly CultureInfo WidgetCulture = CultureInfo.GetCultureInfo("zh-CN");

    private readonly DispatcherTimer _widgetsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<WidgetKind, WidgetVisual> _widgetVisuals = [];
    private bool _widgetsInitialized;
    private WidgetKind? _activePanelKind;

    // 备忘录和待办数据持久化
    private static readonly string NotesPath = GetSettingsPath("notes.json");
    private static readonly string TodoPath = GetSettingsPath("todo.json");
    private static readonly JsonSerializerOptions NotesJsonOptions = new() { WriteIndented = true };
    private static List<NoteItem> _notes = [];
    private static List<TodoItem> _todos = [];
    private int? _editingNoteId;

    private DateTime _lastCpuSampleTime;
    private long _lastCpuIdle, _lastCpuKernel, _lastCpuUser;

    private static readonly HttpClient WeatherHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    private WeatherSnapshot _weather = new();
    private bool _weatherFetching;
    private bool _weatherLocateStarted;
    private double _weatherLat, _weatherLon;
    private string _weatherLocation = "";

    private sealed class WidgetVisual
    {
        public required WidgetKind Kind { get; init; }
        public required Button Host { get; init; }
        public required TextBlock Caption { get; init; }
        public MediaControlButtons? MediaControls { get; init; }
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

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var (glyph, name) = GetWidgetIdentity(kind);

            var glyphBlock = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var caption = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(glyphBlock);
            content.Children.Add(caption);

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

            MediaControlButtons? controls = null;
            if (kind == WidgetKind.AudioPlayer)
            {
                controls = new MediaControlButtons
                {
                    Prev = new Button { Content = "⏮", Width = 40, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false },
                    Play = new Button { Content = "▶", Width = 48, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false },
                    Next = new Button { Content = "⏭", Width = 40, Height = 30, IsEnabled = false },
                };
                controls.Prev.Click += async (_, _) => await MediaPreviousAsync();
                controls.Play.Click += async (_, _) => await MediaPlayPauseAsync();
                controls.Next.Click += async (_, _) => await MediaNextAsync();
            }

            var visual = new WidgetVisual
            {
                Kind = kind,
                Host = host,
                Caption = caption,
                MediaControls = controls,
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
        _widgetsTimer.Tick += (_, _) => TickWidgets();
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
        var foreground = (Brush?)TryFindResource("ForegroundBrush") ?? Brushes.White;

        foreach (var visual in _widgetVisuals.Values)
        {
            visual.Settings = enabled
                ? host?.Items.FirstOrDefault(w => w.Kind == visual.Kind) ?? WidgetSettings.CreateDefault(visual.Kind)
                : null;
            var show = visual.Settings is { Enabled: true, DockDocked: true };
            visual.Host.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            visual.Caption.Foreground = foreground;
            if (show)
            {
                visual.Host.LayoutTransform = new ScaleTransform(visual.Settings!.Size, visual.Settings.Size);
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
            if (_activePanelKind == visual.Kind && visual.Kind is WidgetKind.Clock or WidgetKind.Date or WidgetKind.SystemMonitor or WidgetKind.Calendar)
            {
                ShowWidgetPanel(visual, force: true);
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
        _activePanelKind = visual.Kind;
        DockWidgetPanel.Content = BuildWidgetPanel(visual);
        DockWidgetPanel.Visibility = Visibility.Visible;
    }

    private void CloseWidgetPanel()
    {
        _activePanelKind = null;
        _editingNoteId = null;
        DockWidgetPanel.Content = null;
        DockWidgetPanel.Visibility = Visibility.Collapsed;
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
            WidgetKind.Clock or WidgetKind.Date or WidgetKind.SystemMonitor or WidgetKind.Calendar
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
            LineHeight = 30,
            Text = RenderWidgetDetail(kind),
        };
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

        var cityInput = new TextBox
        {
            Width = 180,
            Height = 30,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "输入城市名，例如：广州",
            Background = new SolidColorBrush(Color.FromArgb(255, 50, 52, 58)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 124, 135)),
            BorderThickness = new Thickness(1),
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
        if (visual.MediaControls is { } mc)
        {
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
        }

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(info);
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
                v.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(note.Title) ? "(无标题)" : note.Title, FontSize = 13, Foreground = Brushes.White });
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

        var titleInput = new TextBox
        {
            Width = 220,
            Height = 24,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "标题",
        };
        var contentInput = new TextBox
        {
            Width = 220,
            Height = 48,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            AcceptsReturn = true,
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

        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        editRow.Children.Add(new TextBlock { Text = "标题 ", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        editRow.Children.Add(titleInput);
        editRow.Children.Add(new TextBlock { Text = " 内容 ", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        editRow.Children.Add(contentInput);
        editRow.Children.Add(saveBtn);

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

        var input = new TextBox
        {
            Width = 220,
            Height = 30,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "新待办内容",
            Background = new SolidColorBrush(Color.FromArgb(255, 50, 52, 58)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 124, 135)),
            BorderThickness = new Thickness(1),
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

    private static (string Glyph, string Name) GetWidgetIdentity(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => ("🕐", "时钟"),
        WidgetKind.Date => ("📅", "日期"),
        WidgetKind.SystemMonitor => ("📊", "监控"),
        WidgetKind.Weather => ("⛅", "天气"),
        WidgetKind.AudioPlayer => ("🎵", "音频"),
        WidgetKind.Notes => ("📝", "备忘"),
        WidgetKind.Todo => ("✅", "待办"),
        WidgetKind.Calendar => ("📆", "日历"),
        _ => ("❔", "小组件"),
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
            WidgetKind.Calendar => $"日历 {now:yyyy年M月}\n{now.ToString("M月d日 dddd", WidgetCulture)}",
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
                await LocateCityAsync();
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
            using var response = await WeatherHttp.GetAsync(
                "https://nominatim.openstreetmap.org/search?q=" + Uri.EscapeDataString(city) + "&format=json&limit=1",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetArrayLength() > 0)
            {
                var first = root[0];
                if (first.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number
                    && first.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number)
                {
                    _weatherLat = lat.GetDouble();
                    _weatherLon = lon.GetDouble();
                    var display = first.TryGetProperty("display_name", out var dn) ? dn.GetString() : "";
                    _weatherLocation = string.IsNullOrWhiteSpace(display) ? city : display.Split(',')[0];
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateMediaButtons);
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
