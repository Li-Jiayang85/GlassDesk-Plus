using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock;

/// <summary>
/// 「程序坞 / 信息栏摆在哪」的唯一计算者。
/// </summary>
/// <remarks>
/// 输入是<strong>物理像素</strong>的显示器工作区与窗口尺寸，输出是物理像素矩形；
/// 偏移量（DIP）在内部按<strong>目标显示器自己的 DPI</strong> 换算，不用系统 DPI——
/// 跨屏换算用错 DPI 正是阶段 2 实测的副屏偏移根源。
///
/// <para>
/// 位置模型是自由坐标：<see cref="SurfacePosition"/> 给出 0–1 的水平/垂直比例
/// 与 DIP 偏移。比例作用在「工作区扣掉面板尺寸后的可移动空间」上，
/// 所以 0 永远贴左/上边、1 永远贴右/下边、0.5 永远居中，和分辨率无关；
/// 六个旧锚点只是把这套比例写死的预设。
/// </para>
///
/// <para>
/// 计算是纯函数：不碰窗口、不碰互操作，同一个输入永远得到同一个矩形。
/// 因此任意比例 × 多屏的正确性可以离线单测；真窗口的实机回归只负责验证
/// 「WPF 窗口确实按算出的矩形就位」这一层。
/// </para>
///
/// <para>
/// 结果保证<strong>完全落在工作区内</strong>（D-15）：屏幕装不下时窗口被夹回
/// 工作区边缘，绝不越界。固定项多到一行放不下的溢出，靠内容滚动解决
/// （批次 3 的滚动容器），不靠把窗口摆出屏。
/// </para>
/// </remarks>
public static class DockPlacementCalculator
{
    /// <summary>
    /// 按自由位置算出窗口的目标矩形（物理像素）。
    /// </summary>
    /// <param name="workArea">目标显示器的工作区（避开任务栏），物理像素。</param>
    /// <param name="position">比例 + DIP 偏移；内部会归一化，越界值不会把窗口摆出屏。</param>
    /// <param name="windowWidthPixels">窗口宽，物理像素。</param>
    /// <param name="windowHeightPixels">窗口高，物理像素。</param>
    /// <param name="dpi">目标显示器的 DPI。偏移量的 DIP→像素换算用它。</param>
    /// <param name="insetPixels">
    /// 面板与工作区边缘的固定内缩（物理像素）。信息栏用它保留 18 DIP 的贴边间隙；
    /// 程序坞传 0，保持真正贴边。
    /// </param>
    public static PixelRect Calculate(
        PixelRect workArea,
        SurfacePosition position,
        int windowWidthPixels,
        int windowHeightPixels,
        uint dpi,
        int insetPixels = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowWidthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHeightPixels);
        ArgumentNullException.ThrowIfNull(position);

        var area = Inset(workArea, Math.Max(0, insetPixels));

        // 比例落在「可移动空间」上：贴边时正好对齐，居中时不因奇偶像素差 1。
        var travelX = area.Width - windowWidthPixels;
        var travelY = area.Height - windowHeightPixels;
        var ratio = position.NormalizedClone();

        var x = area.X + Floor(ratio.HorizontalRatio * travelX) + ToPixels(ratio.OffsetXDip, dpi);
        var y = area.Y + Floor(ratio.VerticalRatio * travelY) + ToPixels(ratio.OffsetYDip, dpi);

        return ClampToWorkArea(new PixelRect(x, y, windowWidthPixels, windowHeightPixels), workArea);
    }

    /// <summary>
    /// 按旧的六个停靠锚点算出目标矩形（物理像素）。
    /// </summary>
    /// <remarks>
    /// 这是 <see cref="Calculate(PixelRect, SurfacePosition, int, int, uint, int)"/> 的兼容外壳：
    /// 锚点被翻译成比例，偏移仍然照旧叠加。保留它让旧调用点与旧单测按原语义工作。
    /// </remarks>
    public static PixelRect Calculate(
        PixelRect workArea,
        DockPlacement placement,
        double offsetXDips,
        double offsetYDips,
        int windowWidthPixels,
        int windowHeightPixels,
        uint dpi)
    {
        var position = SurfacePosition.ForDockPlacement(placement);
        position.OffsetXDip = offsetXDips;
        position.OffsetYDip = offsetYDips;
        return Calculate(workArea, position, windowWidthPixels, windowHeightPixels, dpi);
    }

    /// <summary>把工作区四边内缩指定像素；内缩过多时退回原始工作区。</summary>
    public static PixelRect Inset(PixelRect workArea, int insetPixels)
    {
        if (insetPixels <= 0) return workArea;
        var width = workArea.Width - (insetPixels * 2);
        var height = workArea.Height - (insetPixels * 2);
        if (width <= 0 || height <= 0) return workArea;
        return new PixelRect(workArea.X + insetPixels, workArea.Y + insetPixels, width, height);
    }

    /// <summary>
    /// 把矩形夹回工作区：完全落在工作区内（D-15）。
    /// </summary>
    /// <remarks>
    /// 装不下的极端情形（窗口比工作区还宽/高）以工作区原点为准对齐，
    /// 并截到工作区尺寸——绝不把窗口摆出屏。
    /// </remarks>
    public static PixelRect ClampToWorkArea(PixelRect rect, PixelRect workArea)
    {
        var width = Math.Min(rect.Width, workArea.Width);
        var height = Math.Min(rect.Height, workArea.Height);

        var x = rect.X;
        var y = rect.Y;

        if (x < workArea.X)
        {
            x = workArea.X;
        }
        else if (x + width > workArea.Right)
        {
            x = workArea.Right - width;
        }

        if (y < workArea.Y)
        {
            y = workArea.Y;
        }
        else if (y + height > workArea.Bottom)
        {
            y = workArea.Bottom - height;
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>比例乘法的取整：向下取整，保证 0.5 在奇数余量下与旧的中点算法一致。</summary>
    private static int Floor(double value) => double.IsFinite(value)
        ? (int)Math.Floor(value)
        : 0;

    /// <summary>偏移量的 DIP → 物理像素换算。按目标显示器自己的 DPI。</summary>
    private static int ToPixels(double dips, uint dpi)
        => (int)Math.Round(dips * dpi / 96.0, MidpointRounding.AwayFromZero);
}
