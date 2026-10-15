using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class DockRevealLockTests
{
    [Fact]
    public void LockDoesNotHideAnUncoveredDock()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var controller = InstallController(window);
                window.ToggleRevealLock();
                Assert.Equal(AutoHideState.Shown, controller.State);
                Update(window, covered: false, pointerAtPosition: false);
                Assert.Equal(AutoHideState.Shown, controller.State);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoveredDockCanRevealAtItsRestingPositionOnlyWhenUnlocked(bool fullscreen)
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var controller = InstallController(window);
                window.ToggleRevealLock();
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Hidden, controller.State);
                window.ToggleRevealLock();
                Update(window, covered: true, pointerAtPosition: false, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Hidden, controller.State);
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Shown, controller.State);
                // Once revealed, hovering the visible Dock keeps it available.
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen, pointerOverVisibleDock: true);
                Assert.Equal(AutoHideState.Shown, controller.State);
                window.ToggleRevealLock();
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Hidden, controller.State);
                Update(window, covered: false, pointerAtPosition: false);
                Assert.Equal(AutoHideState.Shown, controller.State);
            }
            finally { window.Close(); }
        });
    }

    private static AutoHideController InstallController(DockWindow window)
    {
        var controller = new AutoHideController(300);
        typeof(DockWindow).GetField("_autoHide", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, controller);
        return controller;
    }

    private static void Update(DockWindow window, bool covered, bool pointerAtPosition,
        bool fullscreen = false, bool pointerOverVisibleDock = false)
    {
        var method = typeof(DockWindow).GetMethod("UpdateAutoHideState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, [DateTime.UtcNow, fullscreen, covered, pointerAtPosition, pointerOverVisibleDock]);
    }
}
