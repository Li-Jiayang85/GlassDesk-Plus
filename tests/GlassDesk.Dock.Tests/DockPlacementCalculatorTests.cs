using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 摆放计算的几何断言。屏幕几何用本机实测的混合 DPI 组合做构造值
/// （主屏 2560×1440 @150%、副屏竖屏 1600×2560 @125% 且带负坐标），
/// 不依赖真机枚举，离线可跑。
/// </summary>
/// <remarks>
/// 真机上的「WPF 窗口按计算结果就位」由 <c>--phase4-acceptance</c> 的实机回归覆盖；
/// 这里锁的是纯函数本身的几何性质。
/// </remarks>
public class DockPlacementCalculatorTests
{
    [Fact]
    public void 纵向停靠左侧并垂直居中()
    {
        var area = new PixelRect(-1600, 0, 1600, 2512);
        var rect = DockPlacementCalculator.Calculate(area, DockPlacement.LeftCenter, 0, 0, 130, 1350, 120);
        Assert.Equal(area.X, rect.X);
        Assert.Equal((area.Height - rect.Height) / 2, rect.Y - area.Y);
    }
    [Fact]
    public void 纵向停靠右侧并垂直居中()
    {
        var area = new PixelRect(-1600, 0, 1600, 2512);
        var rect = DockPlacementCalculator.Calculate(area, DockPlacement.RightCenter, 0, 0, 130, 1350, 120);
        Assert.Equal(area.Right, rect.Right);
        Assert.Equal((area.Height - rect.Height) / 2, rect.Y - area.Y);
    }

    /// <summary>主屏工作区：2560×1440 @150%，任务栏占掉底部 48 物理像素。</summary>
    private static readonly PixelRect PrimaryWorkArea = new(0, 0, 2560, 1392);

    private const uint PrimaryDpi = 144;

    /// <summary>副屏工作区：竖屏 1600×2560 @125%，负 X 坐标（在主屏左侧）。</summary>
    private static readonly PixelRect SecondaryWorkArea = new(-1600, 0, 1600, 2512);

    private const uint SecondaryDpi = 120;

    /// <summary>一个典型 Dock 窗口的物理尺寸（约 900×90 DIP 在 150% 下的样子）。</summary>
    private const int DockWidth = 1350;

    private const int DockHeight = 130;

    [Fact]
    public void 底部居中贴主屏工作区底边且水平居中()
    {
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.Bottom, rect.Bottom);
        Assert.Equal((PrimaryWorkArea.Width - DockWidth) / 2, rect.X - PrimaryWorkArea.X);
    }

    [Fact]
    public void 底部左右贴工作区左右边缘()
    {
        var left = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomLeft, 0, 0, DockWidth, DockHeight, PrimaryDpi);
        Assert.Equal(PrimaryWorkArea.X, left.X);
        Assert.Equal(PrimaryWorkArea.Bottom, left.Bottom);

        var right = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomRight, 0, 0, DockWidth, DockHeight, PrimaryDpi);
        Assert.Equal(PrimaryWorkArea.Right, right.Right);
        Assert.Equal(PrimaryWorkArea.Bottom, right.Bottom);
    }

    [Fact]
    public void 顶部居中贴工作区顶边()
    {
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.TopCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.Y, rect.Y);
        Assert.Equal((PrimaryWorkArea.Width - DockWidth) / 2, rect.X - PrimaryWorkArea.X);
    }

    [Fact]
    public void 四种位置在副屏上都不出屏()
    {
        // 副屏负坐标 + 不同缩放——正负号算错一处的典型翻车现场。
        foreach (var placement in new[] { DockPlacement.BottomCenter, DockPlacement.BottomLeft, DockPlacement.BottomRight, DockPlacement.TopCenter })
        {
            var rect = DockPlacementCalculator.Calculate(
                SecondaryWorkArea, placement, 0, 0, DockWidth, DockHeight, SecondaryDpi);

            Assert.True(rect.X >= SecondaryWorkArea.X, $"{placement}: X {rect.X} < {SecondaryWorkArea.X}");
            Assert.True(rect.Y >= SecondaryWorkArea.Y, $"{placement}: Y {rect.Y} < {SecondaryWorkArea.Y}");
            Assert.True(rect.Right <= SecondaryWorkArea.Right, $"{placement}: 右边越界");
            Assert.True(rect.Bottom <= SecondaryWorkArea.Bottom, $"{placement}: 底边越界");
        }
    }

    [Fact]
    public void 偏移按目标显示器的DPI换算()
    {
        // 100 DIP 在 150% 主屏上是 150 物理像素，在 125% 副屏上是 125——
        // 用系统 DPI（144）去算副屏的偏移正是阶段 2 实测过的错误。
        var baseline = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 0, 0, DockWidth, DockHeight, SecondaryDpi);
        var shifted = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 100, 0, DockWidth, DockHeight, SecondaryDpi);

        Assert.Equal(125, shifted.X - baseline.X);

        var vertical = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 0, -40, DockWidth, DockHeight, SecondaryDpi);
        Assert.Equal(baseline.Bottom - 50, vertical.Bottom);
    }

    [Fact]
    public void 偏移把窗口推出工作区时被夹回()
    {
        var pushed = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomLeft, -100_000, 100_000, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.X, pushed.X);
        Assert.True(pushed.Bottom <= PrimaryWorkArea.Bottom);
        Assert.True(pushed.Right <= PrimaryWorkArea.Right);
    }

    [Fact]
    public void 窗口比工作区还宽时截到工作区尺寸()
    {
        var huge = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomCenter, 0, 0, 4_000, 130, SecondaryDpi);

        Assert.Equal(SecondaryWorkArea.Width, huge.Width);
        Assert.Equal(SecondaryWorkArea.X, huge.X);
    }

    [Fact]
    public void 主屏任务栏上方落位_底部位置避开任务栏()
    {
        // 工作区已经把任务栏减掉了（1392 = 1440 - 48），
        // 所以「贴工作区底边」就等于「任务栏上方」。
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(1392, rect.Bottom);
        Assert.Equal(1440 - 48, PrimaryWorkArea.Bottom);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.5, 0.5)]
    [InlineData(1d, 1d)]
    public void 比例定位覆盖可移动空间(double horizontal, double vertical)
    {
        var position = new SurfacePosition { HorizontalRatio = horizontal, VerticalRatio = vertical };
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, position, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.X + (int)Math.Floor(horizontal * (PrimaryWorkArea.Width - DockWidth)), rect.X);
        Assert.Equal(PrimaryWorkArea.Y + (int)Math.Floor(vertical * (PrimaryWorkArea.Height - DockHeight)), rect.Y);
    }

    [Fact]
    public void 自由比例与旧锚点预设等价()
    {
        foreach (var placement in Enum.GetValues<DockPlacement>())
        {
            var legacy = DockPlacementCalculator.Calculate(
                PrimaryWorkArea, placement, 0, 0, DockWidth, DockHeight, PrimaryDpi);
            var free = DockPlacementCalculator.Calculate(
                PrimaryWorkArea, SurfacePosition.ForDockPlacement(placement), DockWidth, DockHeight, PrimaryDpi);
            Assert.Equal(legacy, free);
        }
    }

    [Fact]
    public void 比例定位也按目标显示器DPI换算偏移()
    {
        var position = new SurfacePosition
        {
            HorizontalRatio = 1,
            VerticalRatio = 1,
            OffsetXDip = -100,
            OffsetYDip = -40,
        };
        var rect = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, position, DockWidth, DockHeight, SecondaryDpi);

        Assert.Equal(SecondaryWorkArea.Right - DockWidth - 125, rect.X);
        Assert.Equal(SecondaryWorkArea.Bottom - DockHeight - 50, rect.Y);
    }

    [Fact]
    public void 信息栏内缩后贴右边并保留十八DIP间隙()
    {
        var area = new PixelRect(0, 0, 1920, 1040);
        var inset = 18;
        var position = SurfacePosition.ForStatusBarEdge(StatusBarEdge.Top);
        var rect = DockPlacementCalculator.Calculate(area, position, 475, 68, 96, inset);

        Assert.Equal(area.Right - inset - 475, rect.X);
        Assert.Equal(area.Y + inset, rect.Y);
    }

    [Fact]
    public void 比例定位加上偏移后仍被夹回工作区()
    {
        var position = new SurfacePosition
        {
            HorizontalRatio = 1,
            VerticalRatio = 1,
            OffsetXDip = SurfacePosition.MaximumOffsetDip,
            OffsetYDip = SurfacePosition.MaximumOffsetDip,
        };
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, position, DockWidth, DockHeight, PrimaryDpi);

        Assert.True(rect.Right <= PrimaryWorkArea.Right);
        Assert.True(rect.Bottom <= PrimaryWorkArea.Bottom);
    }

    [Fact]
    public void 旧配置按停靠锚点与偏移迁移成比例位置()
    {
        var dock = new DockSettings
        {
            Placement = DockPlacement.BottomRight,
            OffsetX = 25,
            OffsetY = -10,
            StatusBarEdge = StatusBarEdge.Bottom,
            StatusBarOffsetX = -120,
            StatusBarOffsetY = 30,
        };

        var position = dock.EffectivePosition();
        Assert.Equal(1, position.HorizontalRatio);
        Assert.Equal(1, position.VerticalRatio);
        // 程序坞已经没有 DIP 微调：旧配置里的偏移会被丢弃。
        Assert.Equal(0, position.OffsetXDip);
        Assert.Equal(0, position.OffsetYDip);
        Assert.Equal(DockOrientation.Horizontal, dock.EffectiveOrientation());

        var statusBar = dock.EffectiveStatusBarPosition();
        Assert.Equal(1, statusBar.HorizontalRatio);
        Assert.Equal(1, statusBar.VerticalRatio);
        Assert.Equal(-120, statusBar.OffsetXDip);
        Assert.Equal(30, statusBar.OffsetYDip);

        var verticalDock = new DockSettings { Placement = DockPlacement.LeftCenter };
        Assert.Equal(DockOrientation.Vertical, verticalDock.EffectiveOrientation());
        Assert.Equal(0, verticalDock.EffectivePosition().HorizontalRatio);
        Assert.Equal(0.5, verticalDock.EffectivePosition().VerticalRatio);
    }

    [Fact]
    public void 比例与偏移越界时被归一化()
    {
        var position = new SurfacePosition
        {
            HorizontalRatio = 5,
            VerticalRatio = -2,
            OffsetXDip = 500_000,
            OffsetYDip = double.NaN,
        };
        position.Normalize();

        Assert.Equal(SurfacePosition.MaximumRatio, position.HorizontalRatio);
        Assert.Equal(SurfacePosition.MinimumRatio, position.VerticalRatio);
        Assert.Equal(SurfacePosition.MaximumOffsetDip, position.OffsetXDip);
        Assert.Equal(0, position.OffsetYDip);
    }
}
