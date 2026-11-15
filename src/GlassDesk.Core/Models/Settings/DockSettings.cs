namespace GlassDesk.Core.Models.Settings;

/// <summary>程序坞停在哪里、以及它如何呈现。</summary>
public enum DockPlacement
{
    BottomCenter = 0,
    BottomLeft = 1,
    BottomRight = 2,
    TopCenter = 3,
    LeftCenter = 4,
    RightCenter = 5,
}

/// <summary>桌面信息栏附着的屏幕边缘。</summary>
public enum StatusBarEdge
{
    Top = 0,
    Bottom = 1,
}

public sealed class DockSettings
{
    public const double FixedIconSize = 48;
    public const double FixedIconSpacing = 8;
    public const double FixedMagnificationPeak = 1.15;
    public const double MinimumFrameLengthDip = 180;
    public const double MaximumFrameLengthDip = 10000;
    public const double MinimumSizeScale = 2d / 3;
    public const double MaximumSizeScale = 1.5;
    public const double MinimumStatusBarSizeScale = 0.75;
    public const double MaximumStatusBarSizeScale = 1.5;

    /// <summary>整体尺寸倍率，同时调整图标、间距与栏厚度。旧配置默认保持原尺寸。</summary>
    public double SizeScale { get; set; } = 1;

    /// <summary>内置入口固定在应用左侧；旧配置默认显示全部入口。</summary>
    public bool ShowLauncherEntry { get; set; } = true;
    public bool ShowThisPcEntry { get; set; } = true;
    public bool ShowDesktopIconsEntry { get; set; } = true;
    public bool ShowNearbyEntry { get; set; } = true;
    public string? NearbyCustomIconPath { get; set; }
    public string? LauncherCustomIconPath { get; set; }
    public string? ThisPcCustomIconPath { get; set; }
    public string? DesktopIconsCustomIconPath { get; set; }

    /// <summary>右上角桌面信息栏；旧配置缺少此项时默认启用。</summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>日期面板以农历为主显示；旧配置默认保留公历。</summary>
    public bool CalendarShowLunar { get; set; }

    /// <summary>实时网速默认关闭；读取配置时保留用户已保存的选择。</summary>
    public bool ShowStatusBarNetworkSpeed { get; set; }

    /// <summary>信息栏第二行的可选项目；空数组表示隐藏全部摘要。</summary>
    public string[] StatusBarSecondaryItems { get; set; } = StatusBarSecondaryItemCatalog.DefaultItems();

    /// <summary>null 为自动选择；手动选择的稳定物理设备键在离线时仍保留。</summary>
    public string? StatusBarMouseDeviceKey { get; set; }
    public string? StatusBarHeadsetDeviceKey { get; set; }
    public string? StatusBarKeyboardDeviceKey { get; set; }

    /// <summary>桌面信息栏的独立缩放倍率，不影响 Dock 图标和外框。</summary>
    public double StatusBarSizeScale { get; set; } = 1;

    /// <summary>桌面信息栏所在的屏幕边缘（预设）。写入后同时更新 <see cref="StatusBarPosition"/> 的垂直比例。</summary>
    public StatusBarEdge StatusBarEdge
    {
        get => _statusBarEdge;
        set
        {
            _statusBarEdge = value;
            if (StatusBarPosition is { } position && Enum.IsDefined(value))
                position.VerticalRatio = value == StatusBarEdge.Bottom ? 1 : 0;
        }
    }

    private StatusBarEdge _statusBarEdge = StatusBarEdge.Top;

    /// <summary>桌面信息栏的水平偏移，单位为 DIP。读写都同步到 <see cref="StatusBarPosition"/>。</summary>
    public double StatusBarOffsetX
    {
        get => _statusBarOffsetX;
        set
        {
            _statusBarOffsetX = value;
            if (StatusBarPosition is { } position && double.IsFinite(value)) position.OffsetXDip = value;
        }
    }

    private double _statusBarOffsetX;

    /// <summary>桌面信息栏的垂直偏移，单位为 DIP。读写都同步到 <see cref="StatusBarPosition"/>。</summary>
    public double StatusBarOffsetY
    {
        get => _statusBarOffsetY;
        set
        {
            _statusBarOffsetY = value;
            if (StatusBarPosition is { } position && double.IsFinite(value)) position.OffsetYDip = value;
        }
    }

    private double _statusBarOffsetY;

    /// <summary>
    /// 桌面信息栏的自由位置（比例 + DIP 偏移）。null 表示旧配置，读取时按
    /// <see cref="StatusBarEdge"/> 与 <see cref="StatusBarOffsetX"/>/<see cref="StatusBarOffsetY"/> 迁移。
    /// </summary>
    public SurfacePosition? StatusBarPosition { get; set; }

    /// <summary>
    /// Dock 玻璃外框的横向长度，单位为 DIP。零表示继续按内容自动调整，
    /// 正数表示固定外框长度；图标尺寸与 Dock 厚度不受此值影响。
    /// </summary>
    public double FrameLengthDip { get; set; }

    /// <summary>图标边长，单位为 DIP。</summary>
    public double IconSize { get; set; } = FixedIconSize;

    /// <summary>图标之间的间隙，单位为 DIP。</summary>
    public double IconSpacing { get; set; } = FixedIconSpacing;

    /// <summary>光标处的放大峰值，以 <see cref="IconSize"/> 为基准的倍数。</summary>
    public double MagnificationPeak { get; set; } = 1.35;

    /// <summary>旧的停靠锚点预设。几何计算改用 <see cref="Position"/>，此处写入会同步成对应比例。</summary>
    public DockPlacement Placement
    {
        get => _placement;
        set
        {
            _placement = value;
            if (!Enum.IsDefined(value)) return;
            var preset = SurfacePosition.ForDockPlacement(value);
            if (Position is not { } position) return;
            position.HorizontalRatio = preset.HorizontalRatio;
            position.VerticalRatio = preset.VerticalRatio;
            Orientation = SurfacePosition.OrientationFor(value);
        }
    }

    private DockPlacement _placement = DockPlacement.BottomCenter;

    /// <summary>
    /// 程序坞的自由位置（比例 + DIP 偏移）。null 表示旧配置，读取时按
    /// <see cref="Placement"/> 与 <see cref="OffsetX"/>/<see cref="OffsetY"/> 迁移。
    /// </summary>
    public SurfacePosition? Position { get; set; }

    /// <summary>
    /// 程序坞是横向条还是纵向条。null 表示旧配置，读取时由 <see cref="Placement"/> 推导：
    /// 左/右居中为纵向，其余为横向。
    /// </summary>
    public DockOrientation? Orientation { get; set; }

    /// <summary>水平微调量，单位为 DIP。读写都同步到 <see cref="Position"/>。</summary>
    public double OffsetX
    {
        get => _offsetX;
        set
        {
            _offsetX = value;
            if (Position is { } position && double.IsFinite(value)) position.OffsetXDip = value;
        }
    }

    private double _offsetX;

    /// <summary>垂直微调量，单位为 DIP。读写都同步到 <see cref="Position"/>。</summary>
    public double OffsetY
    {
        get => _offsetY;
        set
        {
            _offsetY = value;
            if (Position is { } position && double.IsFinite(value)) position.OffsetYDip = value;
        }
    }

    private double _offsetY;

    /// <summary>平时自动收起；鼠标移到程序坞原位置时唤出，离开后延迟收起。</summary>
    public bool AutoHide { get; set; }

    /// <summary>
    /// 自动隐藏的程序坞收起前的缓冲时间。没有它，指针划过边缘时程序坞
    /// 会来回闪烁；方案要求约 300 ms。
    /// </summary>
    public int AutoHideLeaveDelayMs { get; set; } = 300;

    /// <summary>把指针顶到屏幕边缘即可唤出程序坞。</summary>
    public bool EdgeRevealEnabled { get; set; }

    /// <summary>边缘触发条的厚度，单位为 DIP。</summary>
    public int EdgeHotzoneSize { get; set; } = 14;

    /// <summary>
    /// 当其他窗口盖住程序坞区域时，鼠标移入程序坞原位置属于明确悬停操作，仍会唤出。
    /// 该设置原字段保留兼容既有配置；显式边缘唤出锁仍拥有最高优先级。
    /// </summary>
    public bool BlockRevealWhenCovered { get; set; } = true;

    /// <summary>
    /// 旧版本 Dock 显示器选择器的兼容字段。生产 Dock 始终使用进程启动时的 Primary Monitor，
    /// 不读取此字段进行放置。
    /// </summary>
    public string? TargetDisplayId { get; set; }

    /// <summary>在拥有运行中窗口的图标下方显示圆点。</summary>
    public bool ShowRunningIndicator { get; set; } = true;

    /// <summary>
    /// 当前生效的程序坞位置。程序坞只按比例落位，没有 DIP 微调：
    /// 旧配置里的 <see cref="OffsetX"/>/<see cref="OffsetY"/> 以及旧存档中
    /// <see cref="Position"/> 带有的偏移都会被丢弃。
    /// </summary>
    public SurfacePosition EffectivePosition()
    {
        var position = Position is not null
            ? Position.NormalizedClone()
            : SurfacePosition.ForDockPlacement(Enum.IsDefined(Placement) ? Placement : DockPlacement.BottomCenter);
        position.OffsetXDip = 0;
        position.OffsetYDip = 0;
        return position;
    }

    /// <summary>当前生效的信息栏位置；旧配置按上下边缘预设与旧偏移迁移，结果已归一化。</summary>
    public SurfacePosition EffectiveStatusBarPosition()
    {
        if (StatusBarPosition is not null) return StatusBarPosition.NormalizedClone();
        var position = SurfacePosition.ForStatusBarEdge(
            Enum.IsDefined(StatusBarEdge) ? StatusBarEdge : StatusBarEdge.Top);
        position.OffsetXDip = ClampOffset(StatusBarOffsetX);
        position.OffsetYDip = ClampOffset(StatusBarOffsetY);
        return position;
    }

    /// <summary>当前生效的程序坞方向；旧配置由停靠锚点推导。</summary>
    public DockOrientation EffectiveOrientation() => Orientation is { } orientation && Enum.IsDefined(orientation)
        ? orientation
        : SurfacePosition.OrientationFor(Enum.IsDefined(Placement) ? Placement : DockPlacement.BottomCenter);

    private static double ClampOffset(double value) => double.IsFinite(value)
        ? Math.Clamp(value, -SurfacePosition.MaximumOffsetDip, SurfacePosition.MaximumOffsetDip)
        : 0;
}
