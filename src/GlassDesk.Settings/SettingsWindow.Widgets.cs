using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _loadingWidgetsControls = true;
    private DispatcherTimer? _widgetsSaveTimer;

    /// <summary>刷新频率下拉项对应的毫秒值（低帧率节流）。</summary>
    private static readonly int[] WidgetRefreshOptions = [1000, 2000, 5000];

    /// <summary>天气等低频小组件的刷新频率选项。</summary>
    private static readonly int[] WidgetSlowRefreshOptions = [5000, 10000, 30000];

    private void LoadWidgetsControls()
    {
        _loadingWidgetsControls = true;
        try
        {
            var current = _dataStore?.Settings.Current;
            WidgetsEnabledCheck.IsChecked = current?.WidgetsEnabled ?? true;
            WidgetsEnabledCheck.IsEnabled = _dataStore is not null;
            var enabled = _dataStore is not null && WidgetsEnabledCheck.IsChecked == true;

            LoadWidgetControls(WidgetKind.Clock,
                WidgetClockEnabledCheck, WidgetClockSizeSlider, WidgetClockDockCheck, WidgetClockRefreshCombo, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Date,
                WidgetDateEnabledCheck, WidgetDateSizeSlider, WidgetDateDockCheck, WidgetDateRefreshCombo, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.SystemMonitor,
                WidgetMonitorEnabledCheck, WidgetMonitorSizeSlider, WidgetMonitorDockCheck, WidgetMonitorRefreshCombo, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Weather,
                WidgetWeatherEnabledCheck, WidgetWeatherSizeSlider, WidgetWeatherDockCheck, WidgetWeatherRefreshCombo, enabled, current, WidgetSlowRefreshOptions);
            LoadWidgetControls(WidgetKind.AudioPlayer,
                WidgetAudioEnabledCheck, WidgetAudioSizeSlider, WidgetAudioDockCheck, null, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Notes,
                WidgetNotesEnabledCheck, WidgetNotesSizeSlider, WidgetNotesDockCheck, null, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Todo,
                WidgetTodoEnabledCheck, WidgetTodoSizeSlider, WidgetTodoDockCheck, null, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Calendar,
                WidgetCalendarEnabledCheck, WidgetCalendarSizeSlider, WidgetCalendarDockCheck, null, enabled, current, WidgetRefreshOptions);

            UpdateWidgetSizeReadouts();
        }
        finally { _loadingWidgetsControls = false; }
    }

    private static void LoadWidgetControls(WidgetKind kind,
        CheckBox enabledCheck, Slider sizeSlider, CheckBox dockCheck, ComboBox? refreshCombo,
        bool enabled, GlassDeskSettings? current, int[] refreshOptions)
    {
        var widget = current?.Widgets.FirstOrDefault(w => w.Kind == kind);
        enabledCheck.IsChecked = widget?.Enabled ?? true;
        enabledCheck.IsEnabled = enabled;
        sizeSlider.Value = widget?.Size ?? 1.0;
        sizeSlider.IsEnabled = enabled;
        dockCheck.IsChecked = widget?.DockDocked ?? true;
        dockCheck.IsEnabled = enabled;
        if (refreshCombo is not null)
        {
            refreshCombo.SelectedIndex = IndexOfRefresh(widget?.RefreshIntervalMs ?? refreshOptions[0], refreshOptions);
            refreshCombo.IsEnabled = enabled;
        }
    }

    private static int IndexOfRefresh(int ms, int[] options)
    {
        for (var i = 0; i < options.Length; i++)
        {
            if (ms <= options[i] * 1.5)
            {
                return i;
            }
        }
        return options.Length - 1;
    }

    private void UpdateWidgetSizeReadouts()
    {
        if (WidgetClockSizeReadout is not null)
            WidgetClockSizeReadout.Text = $"{WidgetClockSizeSlider.Value:0.00}×";
        if (WidgetDateSizeReadout is not null)
            WidgetDateSizeReadout.Text = $"{WidgetDateSizeSlider.Value:0.00}×";
        if (WidgetMonitorSizeReadout is not null)
            WidgetMonitorSizeReadout.Text = $"{WidgetMonitorSizeSlider.Value:0.00}×";
        if (WidgetWeatherSizeReadout is not null)
            WidgetWeatherSizeReadout.Text = $"{WidgetWeatherSizeSlider.Value:0.00}×";
        if (WidgetAudioSizeReadout is not null)
            WidgetAudioSizeReadout.Text = $"{WidgetAudioSizeSlider.Value:0.00}×";
        if (WidgetNotesSizeReadout is not null)
            WidgetNotesSizeReadout.Text = $"{WidgetNotesSizeSlider.Value:0.00}×";
        if (WidgetTodoSizeReadout is not null)
            WidgetTodoSizeReadout.Text = $"{WidgetTodoSizeSlider.Value:0.00}×";
        if (WidgetCalendarSizeReadout is not null)
            WidgetCalendarSizeReadout.Text = $"{WidgetCalendarSizeSlider.Value:0.00}×";
    }

    private void OnWidgetsControlChanged(object sender, RoutedEventArgs e) => ScheduleSaveWidgets();

    private void OnWidgetsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析期 Slider 默认值变化会在后续控件创建前触发本事件，
        // 此时 Readout 尚未生成，必须跳过，否则设置窗口一打开就崩。
        if (_loadingWidgetsControls || WidgetClockSizeReadout is null)
        {
            return;
        }
        UpdateWidgetSizeReadouts();
        ScheduleSaveWidgets();
    }

    /// <summary>滑块拖动期间用 400ms 防抖合并保存，避免每帧都写盘并重建 Dock。</summary>
    private void ScheduleSaveWidgets()
    {
        if (_loadingWidgetsControls || _dataStore is null) return;
        _widgetsSaveTimer ??= CreateWidgetsSaveTimer();
        _widgetsSaveTimer.Stop();
        _widgetsSaveTimer.Start();
    }

    private DispatcherTimer CreateWidgetsSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveWidgets();
        };
        return timer;
    }

    private void SaveWidgets()
    {
        if (_loadingWidgetsControls || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var previousEnabled = current.WidgetsEnabled;
        var previousWidgets = current.Widgets;
        try
        {
            current.WidgetsEnabled = WidgetsEnabledCheck.IsChecked == true;
            current.Widgets =
            [
                BuildWidget(WidgetKind.Clock,
                    WidgetClockEnabledCheck, WidgetClockSizeSlider, WidgetClockDockCheck, WidgetClockRefreshCombo, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Date,
                    WidgetDateEnabledCheck, WidgetDateSizeSlider, WidgetDateDockCheck, WidgetDateRefreshCombo, WidgetRefreshOptions),
                BuildWidget(WidgetKind.SystemMonitor,
                    WidgetMonitorEnabledCheck, WidgetMonitorSizeSlider, WidgetMonitorDockCheck, WidgetMonitorRefreshCombo, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Weather,
                    WidgetWeatherEnabledCheck, WidgetWeatherSizeSlider, WidgetWeatherDockCheck, WidgetWeatherRefreshCombo, WidgetSlowRefreshOptions),
                BuildWidget(WidgetKind.AudioPlayer,
                    WidgetAudioEnabledCheck, WidgetAudioSizeSlider, WidgetAudioDockCheck, null, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Notes,
                    WidgetNotesEnabledCheck, WidgetNotesSizeSlider, WidgetNotesDockCheck, null, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Todo,
                    WidgetTodoEnabledCheck, WidgetTodoSizeSlider, WidgetTodoDockCheck, null, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Calendar,
                    WidgetCalendarEnabledCheck, WidgetCalendarSizeSlider, WidgetCalendarDockCheck, null, WidgetRefreshOptions),
            ];
            var error = SettingsPageEditor.Validate(current);
            if (error is not null) throw new InvalidOperationException(error);
            _dataStore.Settings.Save();
        }
        catch (Exception exception)
        {
            current.WidgetsEnabled = previousEnabled;
            current.Widgets = previousWidgets;
            LoadWidgetsControls();
            WidgetsSettingsStatus.Text = "保存失败，已恢复原设置：" + exception.Message;
            return;
        }
        WidgetSettingsHost.Current?.Update(current.WidgetsEnabled, current.Widgets);
        WidgetsSettingsStatus.Text = "已保存并应用。";
    }

    private static WidgetSettings BuildWidget(WidgetKind kind,
        CheckBox enabledCheck, Slider sizeSlider, CheckBox dockCheck, ComboBox? refreshCombo, int[] refreshOptions) => new()
    {
        Kind = kind,
        Enabled = enabledCheck.IsChecked == true,
        Size = Math.Clamp(sizeSlider.Value, WidgetSettings.MinimumSize, WidgetSettings.MaximumSize),
        DockDocked = dockCheck.IsChecked == true,
        RefreshIntervalMs = refreshCombo is null
            ? refreshOptions[0]
            : refreshOptions[Math.Clamp(refreshCombo.SelectedIndex, 0, refreshOptions.Length - 1)],
    };
}
