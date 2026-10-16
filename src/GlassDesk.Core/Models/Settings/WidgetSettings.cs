namespace GlassDesk.Core.Models.Settings;

/// <summary>小组件类型标识。</summary>
public enum WidgetKind
{
    Clock = 0,
    Date = 1,
    SystemMonitor = 2,
    Weather = 3,
    AudioPlayer = 4,
    Notes = 5,
    Todo = 6,
    Calendar = 7,
}

/// <summary>
/// 单个小组件的独立设置：启用、尺寸、是否停靠 Dock、刷新频率。
/// 所有小组件共享同一个模型，持久化在 <see cref="GlassDeskSettings.Widgets"/> 列表中，
/// 全局开关由 <see cref="GlassDeskSettings.WidgetsEnabled"/> 控制。
/// </summary>
public sealed class WidgetSettings
{
    /// <summary>尺寸倍率下限。</summary>
    public const double MinimumSize = 0.75;

    /// <summary>尺寸倍率上限。</summary>
    public const double MaximumSize = 2.0;

    /// <summary>刷新间隔下限（毫秒）：低帧率节流，避免高频重绘。</summary>
    public const int MinimumRefreshMs = 500;

    /// <summary>刷新间隔上限（毫秒）：30 分钟，避免频繁刷新浪费资源。</summary>
    public const int MaximumRefreshMs = 1800000;

    public WidgetKind Kind { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>渲染尺寸倍率。</summary>
    public double Size { get; set; } = 1.0;

    /// <summary>是否停靠在 Dock 栏上显示。</summary>
    public bool DockDocked { get; set; } = true;

    /// <summary>刷新频率（毫秒）。</summary>
    public int RefreshIntervalMs { get; set; } = 1800000;

    /// <summary>
    /// 天气小组件的城市/区（留空表示按 IP 自动定位）。
    /// </summary>
    /// <remarks>
    /// 之前城市只存在于运行期字段里，重启就丢，手动选过也留不住。
    /// 落到设置里之后启动时会自动填写，并优先使用它。
    /// </remarks>
    public string WeatherLocation { get; set; } = "";

    /// <summary>创建某个小组件的默认配置。</summary>
    public static WidgetSettings CreateDefault(WidgetKind kind) => new()
    {
        Kind = kind,
        Enabled = true,
        Size = 1.0,
        DockDocked = true,
        RefreshIntervalMs = 1800000,
        WeatherLocation = "",
    };
}
