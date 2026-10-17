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

    /// <summary>
    /// 已移除的日历小组件。**不要在新代码里使用**。
    /// </summary>
    /// <remarks>
    /// 保留这个成员只是因为 `settings.json` 用字符串存枚举（UseStringEnumConverter）；
    /// 直接删掉会让旧配置里的 "Calendar" 反序列化失败，整份设置会被判为损坏而回退。
    /// 加载时 <see cref="GlassDeskSettings.Normalize"/> 会把这条设置丢弃，界面与 Dock 也不再创建它。
    /// </remarks>
    Calendar = 7,
}

/// <summary>小组件类型的可用性判定。</summary>
public static class WidgetKinds
{
    /// <summary>是否仍在产品中提供。已移除的类型为 false。</summary>
    public static bool IsAvailable(WidgetKind kind) => kind != WidgetKind.Calendar;

    /// <summary>界面上提供的全部小组件类型（按固定顺序）。</summary>
    public static WidgetKind[] Available { get; } =
        [.. Enum.GetValues<WidgetKind>().Where(IsAvailable)];
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

    /// <summary>该小组件是否启用。默认关闭（bool 默认值），由用户在设置里逐个打开。</summary>
    public bool Enabled { get; set; }

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

    /// <summary>创建某个小组件的默认配置（默认不启用，等用户自己打开）。</summary>
    public static WidgetSettings CreateDefault(WidgetKind kind) => new()
    {
        Kind = kind,
        Enabled = false,
        Size = 1.0,
        DockDocked = true,
        RefreshIntervalMs = RefreshIntervalFor(kind),
        WeatherLocation = "",
    };

    /// <summary>
    /// 各小组件的固定刷新频率（毫秒）。
    /// </summary>
    /// <remarks>
    /// 刷新频率不再暴露给用户：它是实现细节，选错了只会白白耗电或让数据显得很旧。
    /// 时钟这类每秒才准的取 1 秒；天气来自网络、更新本来就慢，取 30 分钟。
    /// 设置界面已没有对应控件，加载时也会把旧配置里的值纠正到这里。
    /// </remarks>
    public static int RefreshIntervalFor(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => 500,       // 0.5 秒：秒针要准，半秒一跳更接近系统时钟
        WidgetKind.Weather => 1800000, // 30 分钟
        _ => 1000,                     // 1 秒
    };
}
