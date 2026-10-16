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
                WidgetClockEnabledCheck, WidgetClockRefreshCombo, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Date,
                WidgetDateEnabledCheck, WidgetDateRefreshCombo, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.SystemMonitor,
                WidgetMonitorEnabledCheck, WidgetMonitorRefreshCombo, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Weather,
                WidgetWeatherEnabledCheck, WidgetWeatherRefreshCombo, enabled, current, WidgetSlowRefreshOptions);
            // 天气城市：存在设置里，启动时自动填写（留空则按 IP 自动定位）。
            if (WidgetWeatherCityBox is not null)
            {
                WidgetWeatherCityBox.Text = current?.Widgets.FirstOrDefault(w => w.Kind == WidgetKind.Weather)?.WeatherLocation ?? "";
                WidgetWeatherCityBox.IsEnabled = enabled;
            }
            LoadWidgetControls(WidgetKind.AudioPlayer,
                WidgetAudioEnabledCheck, null, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Notes,
                WidgetNotesEnabledCheck, null, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Todo,
                WidgetTodoEnabledCheck, null, enabled, current, WidgetRefreshOptions);
            LoadWidgetControls(WidgetKind.Calendar,
                WidgetCalendarEnabledCheck, null, enabled, current, WidgetRefreshOptions);
        }
        finally { _loadingWidgetsControls = false; }
    }

    private static void LoadWidgetControls(WidgetKind kind,
        CheckBox enabledCheck, ComboBox? refreshCombo,
        bool enabled, GlassDeskSettings? current, int[] refreshOptions)
    {
        var widget = current?.Widgets.FirstOrDefault(w => w.Kind == kind);
        enabledCheck.IsChecked = widget?.Enabled ?? true;
        enabledCheck.IsEnabled = enabled;
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

        private void OnWidgetsControlChanged(object sender, RoutedEventArgs e) => ScheduleSaveWidgets();

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
                    WidgetClockEnabledCheck, WidgetClockRefreshCombo, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Date,
                    WidgetDateEnabledCheck, WidgetDateRefreshCombo, WidgetRefreshOptions),
                BuildWidget(WidgetKind.SystemMonitor,
                    WidgetMonitorEnabledCheck, WidgetMonitorRefreshCombo, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Weather,
                    WidgetWeatherEnabledCheck, WidgetWeatherRefreshCombo, WidgetSlowRefreshOptions,
                    WidgetWeatherCityBox?.Text.Trim() ?? ""),
                BuildWidget(WidgetKind.AudioPlayer,
                    WidgetAudioEnabledCheck, null, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Notes,
                    WidgetNotesEnabledCheck, null, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Todo,
                    WidgetTodoEnabledCheck, null, WidgetRefreshOptions),
                BuildWidget(WidgetKind.Calendar,
                    WidgetCalendarEnabledCheck, null, WidgetRefreshOptions),
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
        CheckBox enabledCheck, ComboBox? refreshCombo, int[] refreshOptions,
        string weatherLocation = "") => new()
    {
        Kind = kind,
        Enabled = enabledCheck.IsChecked == true,
        RefreshIntervalMs = refreshCombo is null
            ? refreshOptions[0]
            : refreshOptions[Math.Clamp(refreshCombo.SelectedIndex, 0, refreshOptions.Length - 1)],
        // 只有天气会传进来，其它小组件恒为空串。
        WeatherLocation = weatherLocation,
    };
}
