using System.Windows.Threading;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private FullscreenWatcher? _fullscreenWatchdog;
    private bool _hiddenByFullscreen;

    /// <summary>退出全屏后的稳定等待期；期间任何一次"又全屏了"都会重新计时。</summary>
    private static readonly TimeSpan FullscreenRestoreDelay = TimeSpan.FromMilliseconds(700);

    private DispatcherTimer? _fullscreenRestoreTimer;

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

        // 退出全屏的恢复要延迟并二次确认——理由见 RestoreAfterFullscreen。
        _fullscreenRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = FullscreenRestoreDelay };
        _fullscreenRestoreTimer.Tick += (_, _) =>
        {
            _fullscreenRestoreTimer?.Stop();
            RestoreAfterFullscreen();
        };

        // 只靠 onFullscreenChanged 不够：它只在「全屏判定」翻转时回调，而最大化不改全屏判定，
        // 回调根本不会来。onStateInvalidated 每个前台/位置事件都会通知，用它驱动让位判定。
        _fullscreenWatchdog = FullscreenWatcher.TryStart(
            OnFullscreenChanged,
            FullscreenOptions.Default,
            onStateInvalidated: () =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => ApplyFullscreenYield(ShouldYieldToForeground())));
            });

        // 启动时可能已经处于全屏（例如从全屏应用里切过来重启外壳），先对齐一次当前状态。
        ApplyFullscreenYield(ShouldYieldToForeground());
    }

    /// <summary>
    /// 前台窗口是否已经占满屏幕、需要把程序坞收走。
    /// </summary>
    /// <remarks>
    /// 只看「真全屏」是不够的：最大化窗口覆盖的是工作区（显示器减去任务栏），
    /// 按整块显示器判定会认为它没占满，置顶的程序坞于是压在最大化窗口上面——
    /// 用户看到的就是「窗口一最大化，程序坞就跳出来」。所以这里把最大化也算作让位条件。
    /// </remarks>
    private bool ShouldYieldToForeground()
    {
        if (FullscreenDetector.IsFullscreenActive())
        {
            return true;
        }

        // 同时拿显示器边界与工作区各判一次：任务栏自动隐藏时工作区等于整屏，
        // 两种口径在这一刻重合，不会互相打架。
        return CurrentPrimaryDisplay is { } display
            && (FullscreenDetector.ForegroundCovers(display.WorkArea)
                || FullscreenDetector.ForegroundCovers(display.Bounds));
    }

    private void StopFullscreenWatchdog()
    {
        _fullscreenWatchdog?.Dispose();
        _fullscreenWatchdog = null;
        _fullscreenRestoreTimer?.Stop();
        _fullscreenRestoreTimer = null;
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        // 看门狗给的布尔值只代表「真全屏」，最大化不在其中；
        // 统一回到 UI 线程后用同一个让位判定重新算一次，避免两套口径打架。
        _ = fullscreen;

        // WinEvent 回调不在 UI 线程上，动窗口前必须先回到 UI 线程。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ApplyFullscreenYield(ShouldYieldToForeground())));
            return;
        }

        ApplyFullscreenYield(ShouldYieldToForeground());
    }

    private void ApplyFullscreenYield(bool fullscreen)
    {
        if (fullscreen)
        {
            // 小组件弹窗是独立窗口，Dock 让位时它也得一起收走，
            // 否则全屏画面上会留着它（它同样是 Topmost）。
            _widgetPopup?.CloseAnimated();
            _widgetPopup = null;

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

        // **不要立刻弹回来。** 全屏应用切前台、弹出自己的菜单、系统弹 UAC 提示，都会让
        // 监视器瞬时报一次"非全屏"；立刻 Show 就是用户看到的"全屏时莫名弹一下"。
        // 性能策略自己有 300 ms 稳定判定，这里同样等状态稳定，到期后再确认真实状态。
        _fullscreenRestoreTimer?.Stop();
        _fullscreenRestoreTimer?.Start();
    }

    /// <summary>等状态稳定后恢复程序坞；恢复前再确认一次当前确实不在全屏。</summary>
    private void RestoreAfterFullscreen()
    {
        if (!_hiddenByFullscreen)
        {
            return;
        }

        // 期间又切回全屏（迟到的状态变化）时不恢复——让下一轮去抖重新计时。
        if (ShouldYieldToForeground())
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
