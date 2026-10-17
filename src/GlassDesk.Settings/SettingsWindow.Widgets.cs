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

    private void LoadWidgetsControls()
    {
        _loadingWidgetsControls = true;
        try
        {
            var current = _dataStore?.Settings.Current;
            // 没有总开关：每个小组件勾不勾就是开不开，控件始终可用（只受数据是否就绪影响）。
            var enabled = _dataStore is not null;

            LoadWidgetControls(WidgetKind.Clock, WidgetClockEnabledCheck, enabled, current);
            LoadWidgetControls(WidgetKind.Date, WidgetDateEnabledCheck, enabled, current);
            LoadWidgetControls(WidgetKind.SystemMonitor, WidgetMonitorEnabledCheck, enabled, current);
            LoadWidgetControls(WidgetKind.Weather, WidgetWeatherEnabledCheck, enabled, current);
            // 天气城市：存在设置里，启动时自动填写（留空则按 IP 自动定位）。
            if (WidgetWeatherCityBox is not null)
            {
                WidgetWeatherCityBox.Text = current?.Widgets.FirstOrDefault(w => w.Kind == WidgetKind.Weather)?.WeatherLocation ?? "";
                WidgetWeatherCityBox.IsEnabled = enabled;
            }
            LoadWidgetControls(WidgetKind.AudioPlayer, WidgetAudioEnabledCheck, enabled, current);
            LoadWidgetControls(WidgetKind.Notes, WidgetNotesEnabledCheck, enabled, current);
            LoadWidgetControls(WidgetKind.Todo, WidgetTodoEnabledCheck, enabled, current);
            LoadWidgetControls(WidgetKind.Calendar, WidgetCalendarEnabledCheck, enabled, current);
        }
        finally { _loadingWidgetsControls = false; }
    }

    /// <summary>
    /// 只回填「是否启用」。刷新频率是固定值（见 <see cref="WidgetSettings.RefreshIntervalFor"/>），
    /// 不再暴露给用户，所以界面上没有对应控件。
    /// </summary>
    private static void LoadWidgetControls(WidgetKind kind,
        CheckBox enabledCheck, bool enabled, GlassDeskSettings? current)
    {
        var widget = current?.Widgets.FirstOrDefault(w => w.Kind == kind);
        enabledCheck.IsChecked = widget?.Enabled ?? false;
        enabledCheck.IsEnabled = enabled;
    }

    private void OnWidgetsControlChanged(object sender, RoutedEventArgs e) => ScheduleSaveWidgets();

    /// <summary>控件变更用 400ms 防抖合并保存，避免每帧都写盘并重建 Dock。</summary>
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
        var previousWidgets = current.Widgets;
        try
        {
            // 总开关已从界面移除；保留字段供旧配置兼容，形态上恒为开。
            current.WidgetsEnabled = true;
            current.Widgets =
            [
                BuildWidget(WidgetKind.Clock, WidgetClockEnabledCheck),
                BuildWidget(WidgetKind.Date, WidgetDateEnabledCheck),
                BuildWidget(WidgetKind.SystemMonitor, WidgetMonitorEnabledCheck),
                BuildWidget(WidgetKind.Weather, WidgetWeatherEnabledCheck,
                    WidgetWeatherCityBox?.Text.Trim() ?? ""),
                BuildWidget(WidgetKind.AudioPlayer, WidgetAudioEnabledCheck),
                BuildWidget(WidgetKind.Notes, WidgetNotesEnabledCheck),
                BuildWidget(WidgetKind.Todo, WidgetTodoEnabledCheck),
                BuildWidget(WidgetKind.Calendar, WidgetCalendarEnabledCheck),
            ];
            var error = SettingsPageEditor.Validate(current);
            if (error is not null) throw new InvalidOperationException(error);
            _dataStore.Settings.Save();
        }
        catch (Exception exception)
        {
            current.Widgets = previousWidgets;
            LoadWidgetsControls();
            WidgetsSettingsStatus.Text = "保存失败，已恢复原设置：" + exception.Message;
            return;
        }
        WidgetSettingsHost.Current?.Update(current.WidgetsEnabled, current.Widgets);
        WidgetsSettingsStatus.Text = "已保存并应用。";
    }

    /// <summary>构建单个小组件的设置；刷新频率取固定值，不来自界面。</summary>
    private static WidgetSettings BuildWidget(WidgetKind kind,
        CheckBox enabledCheck, string weatherLocation = "") => new()
    {
        Kind = kind,
        Enabled = enabledCheck.IsChecked == true,
        RefreshIntervalMs = WidgetSettings.RefreshIntervalFor(kind),
        // 只有天气会传进来，其它小组件恒为空串。
        WeatherLocation = weatherLocation,
    };
}
