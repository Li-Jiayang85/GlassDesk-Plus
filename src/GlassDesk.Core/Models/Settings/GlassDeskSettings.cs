namespace GlassDesk.Core.Models.Settings;

/// <summary>
/// 用户可配置内容的根，持久化为单个 JSON 文档。
/// </summary>
/// <remarks>
/// 这是我们自己的设置模型。它从不读写参考产品的
/// 配置（MacDock 的 <c>%APPDATA%\MacDock\settings.json</c> 或启动台的
/// localStorage），那些配置保持原样，老程序才能继续工作。
/// </remarks>
public sealed class GlassDeskSettings
{
    /// <summary>
    /// 磁盘上文档的 schema 版本。只要某次变更无法仅靠
    /// 默认值吸收就递增；旧文件在加载时迁移。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>null 表示旧版本未记录教学状态；加载时区分新资料和已有资料。</summary>
    public bool? OnboardingDismissed { get; set; }

    public AppearanceSettings Appearance { get; set; } = new();

    // Null identifies a profile whose original Naiwa preferences still live in settings.ini.
    public NaiwaOptions? NaiwaOptions { get; set; }

    public DockSettings Dock { get; set; } = new();

    public LauncherSettings Launcher { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public NearbySettings Nearby { get; set; } = new();

    public DesktopTaskbarSettings DesktopTaskbar { get; set; } = new();

    /// <summary>
    /// 把为 null 的节替换为默认值。反序列化手工编辑过或写入不完整的
    /// 文件时，某个节可能为 null，尽管类型声明并非如此；
    /// 否则每个使用方都得自己做 null 检查。
    /// </summary>
    public void Normalize()
    {
        Appearance ??= new AppearanceSettings();
        NaiwaOptions?.Normalize();
        Dock ??= new DockSettings();
        Dock.StatusBarSecondaryItems = StatusBarSecondaryItemCatalog.Normalize(Dock.StatusBarSecondaryItems);
        Launcher ??= new LauncherSettings();
        Shortcuts ??= new ShortcutSettings();
        Display ??= new DisplaySettings();
        Performance ??= new PerformanceSettings();
        Nearby ??= new NearbySettings();
        Nearby.Normalize();
        DesktopTaskbar ??= new DesktopTaskbarSettings();
        Dock.EdgeRevealEnabled = false;
        Launcher.FullscreenMode = false;
        Shortcuts.HotCornersEnabled = false;
        Shortcuts.MouseSideButtonsEnabled = false;

        // Keep legacy enum values readable while preserving supported material choices.
        Appearance.Theme = ThemeMode.Dark;
        Appearance.NormalizeGlassMaterials();
        Dock.SizeScale = double.IsFinite(Dock.SizeScale)
            ? Math.Clamp(Dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;
        Dock.StatusBarSizeScale = double.IsFinite(Dock.StatusBarSizeScale)
            ? Math.Clamp(Dock.StatusBarSizeScale, DockSettings.MinimumStatusBarSizeScale, DockSettings.MaximumStatusBarSizeScale) : 1;
        if (!Enum.IsDefined(Dock.Placement)) Dock.Placement = DockPlacement.BottomCenter;
        if (!Enum.IsDefined(Dock.StatusBarEdge)) Dock.StatusBarEdge = StatusBarEdge.Top;
        // 位置改成「比例 + DIP 偏移」的自由坐标：旧配置在这里补齐比例，
        // 迁移后再把旧字段镜像回去，其它页面与旧代码读到的仍是同样的数值。
        Dock.Orientation = Dock.EffectiveOrientation();
        Dock.Position = Dock.EffectivePosition();
        Dock.StatusBarPosition = Dock.EffectiveStatusBarPosition();
        Dock.OffsetX = Dock.Position.OffsetXDip;
        Dock.OffsetY = Dock.Position.OffsetYDip;
        Dock.StatusBarOffsetX = Dock.StatusBarPosition.OffsetXDip;
        Dock.StatusBarOffsetY = Dock.StatusBarPosition.OffsetYDip;
        // Settings now owns its opaque surface instead of changing every window.
        Appearance.ReduceTransparency = false;
        Appearance.DockGlassTransparency = double.IsFinite(Appearance.DockGlassTransparency)
            ? Math.Clamp(Appearance.DockGlassTransparency, 0, 1) : AppearanceSettings.DefaultDockGlassTransparency;
        Appearance.StatusBarGlassTransparency = Appearance.StatusBarGlassTransparency is { } statusBarTransparency
            && double.IsFinite(statusBarTransparency) ? Math.Clamp(statusBarTransparency, 0, 1) : Appearance.DockGlassTransparency;
        var controlCenterTransparency = Appearance.GetGlassTransparency(GlassMaterialTarget.ControlCenter);
        Appearance.ControlCenterGlassTransparency = double.IsFinite(controlCenterTransparency)
            ? Math.Clamp(controlCenterTransparency, 0, 1) : AppearanceSettings.DefaultControlCenterGlassTransparency;
        Appearance.LauncherGlassTransparency = double.IsFinite(Appearance.LauncherGlassTransparency)
            ? Math.Clamp(Appearance.LauncherGlassTransparency, 0, 1) : AppearanceSettings.DefaultLauncherGlassTransparency;
        Appearance.DockBuiltinIconBrightness = double.IsFinite(Appearance.DockBuiltinIconBrightness)
            ? Math.Clamp(Appearance.DockBuiltinIconBrightness, 0, 1) : 1;
        Appearance.LauncherTextBrightness = double.IsFinite(Appearance.LauncherTextBrightness)
            ? Math.Clamp(Appearance.LauncherTextBrightness, 0, 1) : 1;
        Appearance.StatusBarTextBrightness = double.IsFinite(Appearance.StatusBarTextBrightness)
            ? Math.Clamp(Appearance.StatusBarTextBrightness, 0, 1) : 1;
        Appearance.GlassTintOpacity = double.IsFinite(Appearance.GlassTintOpacity)
            ? Math.Clamp(Appearance.GlassTintOpacity, 0, 1) : AppearanceSettings.DefaultGlassTintOpacity;
        Appearance.GlassBackgroundClarity = double.IsFinite(Appearance.GlassBackgroundClarity)
            ? Math.Clamp(Appearance.GlassBackgroundClarity, 0, 1) : AppearanceSettings.DefaultGlassBackgroundClarity;

        Shortcuts.Normalize();
        DesktopTaskbar.Normalize();
    }
}
