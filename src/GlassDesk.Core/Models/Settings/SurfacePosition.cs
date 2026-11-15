namespace GlassDesk.Core.Models.Settings;

/// <summary>程序坞的排布方向。</summary>
public enum DockOrientation
{
    Horizontal = 0,
    Vertical = 1,
}

/// <summary>
/// 面板在工作区里的自由位置：先按比例定位，再用 DIP 偏移微调。
/// </summary>
/// <remarks>
/// 比例是「可移动空间」上的相对位置：0 表示贴工作区左/上边，1 表示贴右/下边，
/// 0.5 表示居中。可移动空间指工作区扣掉面板自身尺寸后的区域，所以同一个比例
/// 在 1080p、4K、副屏负坐标上都指向同一条视觉线，与分辨率、DPI 无关。
///
/// <para>
/// 偏移量是定位之外的手动微调，单位 DIP，正值向右/向下。它按目标显示器
/// 自己的 DPI 换算，跨屏时不会像系统 DPI 那样放大。
/// </para>
///
/// <para>
/// 旧版本的六个停靠锚点、信息栏上下边缘仍保留为预设：
/// <see cref="ForDockPlacement"/> 与 <see cref="ForStatusBarEdge"/> 把预设翻译成比例，
/// 读取旧配置时再用旧值的偏移量补齐，因此升级不会让面板跳位。
/// </para>
/// </remarks>
public sealed class SurfacePosition
{
    /// <summary>比例下限：贴左/上边。</summary>
    public const double MinimumRatio = 0d;

    /// <summary>比例上限：贴右/下边。</summary>
    public const double MaximumRatio = 1d;

    /// <summary>手动偏移的允许范围（DIP），正负相同。</summary>
    public const double MaximumOffsetDip = 2000d;

    /// <summary>水平比例：0 贴左，0.5 居中，1 贴右。</summary>
    public double HorizontalRatio { get; set; } = 0.5;

    /// <summary>垂直比例：0 贴上，0.5 居中，1 贴下。</summary>
    public double VerticalRatio { get; set; } = 1;

    /// <summary>水平微调，DIP。正值向右。</summary>
    public double OffsetXDip { get; set; }

    /// <summary>垂直微调，DIP。正值向下。</summary>
    public double OffsetYDip { get; set; }

    /// <summary>六个停靠预设对应的比例；偏移为 0，由调用方补齐旧值。</summary>
    public static SurfacePosition ForDockPlacement(DockPlacement placement) => placement switch
    {
        DockPlacement.BottomLeft => new() { HorizontalRatio = 0, VerticalRatio = 1 },
        DockPlacement.BottomRight => new() { HorizontalRatio = 1, VerticalRatio = 1 },
        DockPlacement.TopCenter => new() { HorizontalRatio = 0.5, VerticalRatio = 0 },
        DockPlacement.LeftCenter => new() { HorizontalRatio = 0, VerticalRatio = 0.5 },
        DockPlacement.RightCenter => new() { HorizontalRatio = 1, VerticalRatio = 0.5 },
        _ => new() { HorizontalRatio = 0.5, VerticalRatio = 1 },
    };

    /// <summary>信息栏上下边缘预设：右侧贴边，垂直方向贴上或贴下。</summary>
    public static SurfacePosition ForStatusBarEdge(StatusBarEdge edge) => new()
    {
        HorizontalRatio = 1,
        VerticalRatio = edge == StatusBarEdge.Bottom ? 1 : 0,
    };

    /// <summary>程序坞方向预设：左右贴边是纵向，其余是横向。</summary>
    public static DockOrientation OrientationFor(DockPlacement placement)
        => placement is DockPlacement.LeftCenter or DockPlacement.RightCenter
            ? DockOrientation.Vertical
            : DockOrientation.Horizontal;

    public SurfacePosition Clone() => new()
    {
        HorizontalRatio = HorizontalRatio,
        VerticalRatio = VerticalRatio,
        OffsetXDip = OffsetXDip,
        OffsetYDip = OffsetYDip,
    };

    /// <summary>把非法值（NaN/∞/越界）夹回允许范围，避免旧配置把窗口摆到屏幕外。</summary>
    public void Normalize()
    {
        HorizontalRatio = double.IsFinite(HorizontalRatio)
            ? Math.Clamp(HorizontalRatio, MinimumRatio, MaximumRatio) : 0.5;
        VerticalRatio = double.IsFinite(VerticalRatio)
            ? Math.Clamp(VerticalRatio, MinimumRatio, MaximumRatio) : 1;
        OffsetXDip = double.IsFinite(OffsetXDip)
            ? Math.Clamp(OffsetXDip, -MaximumOffsetDip, MaximumOffsetDip) : 0;
        OffsetYDip = double.IsFinite(OffsetYDip)
            ? Math.Clamp(OffsetYDip, -MaximumOffsetDip, MaximumOffsetDip) : 0;
    }

    /// <summary>复制一份归一化后的副本，用于验证前的构造。</summary>
    public SurfacePosition NormalizedClone()
    {
        var copy = Clone();
        copy.Normalize();
        return copy;
    }
}
