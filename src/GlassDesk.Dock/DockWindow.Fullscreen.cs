using System.Windows.Threading;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private FullscreenWatcher? _fullscreenWatchdog;
    private bool _hiddenByFullscreen;

    /// <summary>
    /// 全屏让位看门狗：全屏应用出现在前台时把程序坞收走，退出全屏后再恢复。
    /// </summary>
    /// <remarks>
    /// 性能策略本来就会在全屏时抑制程序坞：<c>IsSuppressionRequested</c> 经
    /// <c>AppHost.ReconcilePerformance</c> 走到 <c>SetPerformanceSleeping(true)</c>，
    /// 滑出后由 <c>FinishPerformanceSleep</c> 调 <c>Hide()</c> 收走窗口。
    /// 但那条链路要经过前台观察、300 ms 稳定判定与调度推进，任何一环没走通，
    /// 置顶的窗口就会压在全屏画面上。
    /// <para>
    /// 这里用同一个 <see cref="FullscreenWatcher"/> 再兜一层。它是 <c>SetWinEventHook</c>
    /// 事件驱动：不轮询、不做屏幕捕获，空闲时没有成本，因此可以作为冗余保险常开。
    /// 恢复时只在"确实是本层收走的、且没有别的抑制原因"的前提下 <c>Show()</c>，
    /// 以免与性能策略的手动休眠互相 Show/Hide。
    /// </para>
    /// </remarks>
    private void StartFullscreenWatchdog()
    {
        if (_fullscreenWatchdog is not null)
        {
            return;
        }

        _fullscreenWatchdog = FullscreenWatcher.TryStart(OnFullscreenChanged, FullscreenOptions.Default);

        // 启动时可能已经处于全屏（例如从全屏应用里切过来重启外壳），先对齐一次当前状态。
        ApplyFullscreenYield(FullscreenDetector.IsFullscreenActive());
    }

    private void StopFullscreenWatchdog()
    {
        _fullscreenWatchdog?.Dispose();
        _fullscreenWatchdog = null;
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        // WinEvent 回调不在 UI 线程上，动窗口前必须先回到 UI 线程。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ApplyFullscreenYield(fullscreen)));
            return;
        }

        ApplyFullscreenYield(fullscreen);
    }

    private void ApplyFullscreenYield(bool fullscreen)
    {
        if (fullscreen)
        {
            // 已经不可见（可能刚被性能策略收走）就不动，免得抢它的恢复时机。
            if (!IsVisible)
            {
                return;
            }

            _hiddenByFullscreen = true;
            Hide();
            return;
        }

        if (!_hiddenByFullscreen)
        {
            return;
        }

        _hiddenByFullscreen = false;

        // 还有别的抑制原因（手动休眠、会话挂起、性能策略仍在抑制）时交给那一层恢复，
        // 否则两条路径会各自 Show/Hide 打架。
        if (_performanceSleepRequested || _services is null || CurrentPrimaryDisplay is null)
        {
            return;
        }

        Show();
    }
}
