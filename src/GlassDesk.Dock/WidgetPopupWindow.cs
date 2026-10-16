using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>
/// 小组件弹窗：独立玻璃窗口，从 Dock 上被点击的图标上方弹出。
/// </summary>
/// <remarks>
/// 小组件面板原本是 Dock 的内联部分，于是"打开面板"必然要让 Dock 长高——而 Dock 是
/// 分层玻璃窗口，逐帧改尺寸会让捕获坐标不断变化，玻璃在运动中会降级，那一下高度变化
/// 本身就是肉眼可见的跳动。
/// <para>
/// 改成独立窗口后 Dock 的尺寸全程不变。弹出动画与启动台同源：**先预热该尺寸的玻璃，
/// 再显示并做缩放淡入**（原点在底边中心，看起来是从图标里长出来），因此既不会闪空帧，
/// 也不需要 Dock 让位。
/// </para>
/// </remarks>
internal sealed class WidgetPopupWindow : GlassWindow, IDisposable
{
    /// <summary>起始缩放：从略小长到原大小，与控制中心的观感一致。</summary>
    private const double RevealScale = .92;

    /// <summary>弹窗底边与被点击图标上沿的间距（DIP）。</summary>
    private const double AnchorGap = 10;

    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(130));

    private readonly Grid _root = new() { ClipToBounds = true };
    private readonly LiquidGlassSurface _glass = new();
    private readonly ContentPresenter _host = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private bool _closing;

    /// <summary>锚点（被点击图标）的物理像素矩形。整条定位链路都用物理像素。</summary>
    private Rect _anchorPx;
    private bool _hasAnchor;

    public WidgetPopupWindow()
    {
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 弹出面板是被"点开"的，不该把前台焦点从用户正在用的窗口抢走。
        ShowActivated = false;

        PanelCornerRadius = new CornerRadius(26);

        _root.RenderTransformOrigin = new Point(.5, 1);
        _root.RenderTransform = _scale;
        _root.Children.Add(_glass);
        _root.Children.Add(new Border
        {
            Padding = new Thickness(18, 14, 18, 16),
            Background = Brushes.Transparent,
            Child = _host,
        });
        Content = _root;

        _glass.IsBackdropActive = () => IsVisible;

        // 内容刷新会改变尺寸（时钟每秒跳、媒体每 500ms 刷新），而 SizeToContent 的窗口
        // 只保持左上角不动、向下生长——不重新贴回锚点的话，弹窗就会一次次往下压。
        //
        // 要点是**推迟到本帧的渲染阶段**再校正：WPF 自己在尺寸变化后也会重摆窗口，
        // 若在 SizeChanged 里立刻设位置，会被它随后的重摆覆盖，窗口就一点点漂走。
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Reanchor));
    }

    /// <summary>换一个锚点图标（用户点了另一个小组件），并在本帧布局结束后重新贴合。</summary>
    public void ReanchorTo(FrameworkElement anchor)
    {
        var origin = anchor.PointToScreen(new Point(0, 0));
        var corner = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        _anchorPx = new Rect(origin, corner);
        _hasAnchor = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Reanchor));
    }

    /// <summary>把窗口重新贴回被点击图标的上方：底边贴着图标上沿、水平保持居中。</summary>
    /// <remarks>
    /// **用物理像素摆放，不用 WPF 的 Left/Top。** 代码里对这件事已有实测结论
    /// （见 DockWindow.ApplySlide 的注释）：Left/Top 在混合 DPI 下会偏，Dock 自己
    /// 也是全程用 DisplayService.MoveWindowToPixelRect 摆的。
    /// 锚点来自 PointToScreen（本来就是物理像素），窗口尺寸按设备变换换算成物理像素，
    /// 所以整条链路没有 DIP 参与，多屏不同缩放下也不会跑偏。
    /// 尺寸一变就重新调用，否则 SizeToContent 的窗口会以左上角为锚向下生长。
    /// </remarks>
    private void Reanchor()
    {
        if (!_hasAnchor)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var widthPx = (int)Math.Round(Math.Max(1, ActualWidth) * toDevice.M11);
        var heightPx = (int)Math.Round(Math.Max(1, ActualHeight) * toDevice.M22);
        if (widthPx <= 1 || heightPx <= 1)
        {
            return;
        }

        // 水平对齐图标中心，底边贴在图标上沿（AnchorGap 是 DIP，按缩放换成物理像素）。
        var x = (int)Math.Round(_anchorPx.X + (_anchorPx.Width / 2) - (widthPx / 2.0));
        var y = (int)Math.Round(_anchorPx.Y - heightPx - (AnchorGap * toDevice.M22));

        // 夹进工作区：WorkArea 是 DIP，换算成物理像素后比较。
        var work = SystemParameters.WorkArea;
        var minX = (int)Math.Round((work.Left + 8) * toDevice.M11);
        var maxX = (int)Math.Round((work.Right - 8) * toDevice.M11) - widthPx;
        var minY = (int)Math.Round((work.Top + 8) * toDevice.M22);
        var maxY = (int)Math.Round((work.Bottom - 8) * toDevice.M22) - heightPx;
        x = Math.Clamp(x, minX, Math.Max(minX, maxX));
        y = Math.Clamp(y, minY, Math.Max(minY, maxY));

        DisplayService.MoveWindowToPixelRect(handle, new PixelRect(x, y, widthPx, heightPx));
    }

    public void SetContent(UIElement? content) => _host.Content = content;

    /// <summary>
    /// 摆到锚点图标上方并显示。
    /// </summary>
    /// <remarks>
    /// 顺序与启动台一致：**先以透明显示**（让 SizeToContent 算出真实尺寸）→ 请玻璃面按
    /// 这个尺寸预热材质 → 设置位置 → 再播放展开动画。透明那一步是必要的：既拿到尺寸，
    /// 又不会让用户看到未就位的窗口。
    /// </remarks>
    public void RevealAbove(FrameworkElement anchor, bool animate)
    {
        Opacity = 0;
        Show();
        UpdateLayout();

        // 用窗口自己的设备变换换算物理像素↔DIP，不猜 DPI 数值：
        // PointToScreen 给的是物理像素，而 Window.Left/Top 要 DIP；
        // PrepareRevealMaterial 要的却是物理像素矩形 + 真实 DPI 数值（内部按 96/dpi 换算）。
        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        // 锚点与目标位置全部用物理像素（PointToScreen 本来就是物理像素），
        // 摆放交给 DisplayService，见 Reanchor 的说明。
        var originPx = anchor.PointToScreen(new Point(0, 0));
        var cornerPx = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        _anchorPx = new Rect(originPx, cornerPx);
        _hasAnchor = true;
        Reanchor();

        // 预热：锚点矩形已经是物理像素，面板矩形由窗口当前尺寸换算而来。
        var dpi = Math.Max(1, 96 * toDevice.M11);
        var panelPx = toDevice.Transform(new Point(Left, Top));
        _glass.PrepareRevealMaterial(
            new PixelRect((int)Math.Round(_anchorPx.X), (int)Math.Round(_anchorPx.Y),
                Math.Max(1, (int)Math.Round(_anchorPx.Width)), Math.Max(1, (int)Math.Round(_anchorPx.Height))),
            new PixelRect((int)Math.Round(panelPx.X), (int)Math.Round(panelPx.Y),
                Math.Max(1, (int)Math.Round(ActualWidth * toDevice.M11)), Math.Max(1, (int)Math.Round(ActualHeight * toDevice.M22))),
            dpi);

        if (!animate || ThemeService.Current?.AnimationsEnabled == false)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, RevealDuration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(RevealScale, 1, RevealDuration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(RevealScale, 1, RevealDuration) { EasingFunction = ease });
    }

    /// <summary>播放收起动画后关闭窗口；重复调用只生效一次。</summary>
    public void CloseAnimated()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (!IsVisible || ThemeService.Current?.AnimationsEnabled == false)
        {
            Close();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, HideDuration) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_scale.ScaleX, RevealScale, HideDuration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_scale.ScaleY, RevealScale, HideDuration) { EasingFunction = ease });
    }

    protected override void OnClosed(EventArgs e)
    {
        // 断开玻璃面的回调，避免窗口关闭后还有帧通知进来。
        _glass.IsBackdropActive = null;
        base.OnClosed(e);
        Dispose();
    }

    /// <summary>释放玻璃面（它持有捕获与渲染资源）。</summary>
    public void Dispose()
    {
        _glass.Dispose();
        GC.SuppressFinalize(this);
    }
}
