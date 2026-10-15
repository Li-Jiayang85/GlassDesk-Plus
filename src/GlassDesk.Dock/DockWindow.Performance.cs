using System.Windows.Threading;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private bool _performanceSleepRequested, _performanceSleeping;
    private bool _showAfterPerformanceSleep, _tilesDirty;
    private bool _lastPerformanceSurfaceVisible;
    private bool _lastPerformanceCaptureRequested;
    private bool _backgroundCaptureSleeping;

    public bool IsPerformanceSleeping => _performanceSleeping;
    public bool IsPerformanceSurfaceVisible => IsVisible && IsOpticalSurfaceVisible && !_performanceSleeping;
    /// <summary>Keep wake acquisition admitted before the first slide pixels become visible.</summary>
    public bool IsPerformanceCaptureRequested => IsVisible && !_performanceSleepRequested
        && (IsPerformanceSurfaceVisible || _autoHide?.State is AutoHideState.Shown or AutoHideState.WaitingToHide);
    public event EventHandler? PerformanceSurfaceVisibilityChanged;

    /// <summary>Ordinary obscured idle releases capture but retains the existing pointer reveal detector.</summary>
    public void SetBackgroundCaptureSleeping(bool sleeping)
    {
        Dispatcher.VerifyAccess();
        if (_backgroundCaptureSleeping == sleeping) return;
        _backgroundCaptureSleeping = sleeping;
        GlassSurface.SetPerformanceSleeping(sleeping || _performanceSleepRequested);
        if (!sleeping && !_performanceSleepRequested) PrepareConcealedReveal();
        if (!sleeping || _performanceSleepRequested) return;
        if (_frameTimer is null) RebuildAutoHide();
        _frameTimer!.Interval = TimeSpan.FromMilliseconds(100);
        _frameTimer.Start();
    }

    private void PrepareConcealedReveal()
    {
        if (_restingRect.Width > 0 && _slideOffsetPixels >= _restingRect.Height
            && CurrentPrimaryDisplay is { } display)
            GlassSurface.PrepareRevealMaterial(_restingRect, _restingRect, display.Dpi);
    }

    /// <summary>Finish the ordinary slide before releasing glass; sleeping cannot be passively revealed.</summary>
    public void SetPerformanceSleeping(bool sleeping)
    {
        Dispatcher.VerifyAccess();
        if (_performanceSleepRequested == sleeping) return;
        _performanceSleepRequested = sleeping;
        if (sleeping)
        {
            _showAfterPerformanceSleep = IsVisible;
            ResetEntryHover();
            _failureNoticeTimer?.Stop();
            if (!IsVisible || _services is null || _restingRect.Width == 0
                || ThemeService.Current?.AnimationsEnabled == false)
            {
                FinishPerformanceSleep();
                return;
            }
            _frameTimer!.Interval = FrameInterval;
            _frameTimer.Start();
            OnAutoHideFrame();
            return;
        }
        _performanceSleeping = false;
        GlassSurface.SetPerformanceSleeping(_backgroundCaptureSleeping);
        RebuildAutoHide();
        if (_tilesDirty) { _tilesDirty = false; RefreshTiles(); }
        RefreshDisplayPlacement();
        if (_showAfterPerformanceSleep && CurrentPrimaryDisplay is not null) Show();
        NotifyPerformanceSurfaceVisibility();
    }

    private void AdvancePerformanceSleep()
    {
        if (_performanceSleeping) return;
        _autoHide?.ForceHide(DateTime.UtcNow);
        var target = (double)_fullyHiddenOffsetPixels;
        if (ThemeService.Current?.AnimationsEnabled == false) _slideOffsetPixels = target;
        else _slideOffsetPixels += (target - _slideOffsetPixels) * .25;
        if (Math.Abs(_slideOffsetPixels - target) <= .5) _slideOffsetPixels = target;
        ApplySlide(_restingRect, _slideOffsetPixels);
        NotifyPerformanceSurfaceVisibility();
        if (_slideOffsetPixels == target) FinishPerformanceSleep();
    }

    private void FinishPerformanceSleep()
    {
        _slideOffsetPixels = _fullyHiddenOffsetPixels;
        _performanceSleeping = true;
        _frameTimer?.Stop();
        GlassSurface.SetPerformanceSleeping(true);
        // Hiding the HWND also stops child hover/reorder render loops and prevents hit testing.
        if (IsVisible) Hide();
        NotifyPerformanceSurfaceVisibility();
    }

    private void NotifyPerformanceSurfaceVisibility()
    {
        var visible = IsPerformanceSurfaceVisible;
        var requested = IsPerformanceCaptureRequested;
        if (_lastPerformanceSurfaceVisible == visible && _lastPerformanceCaptureRequested == requested) return;
        _lastPerformanceSurfaceVisible = visible;
        _lastPerformanceCaptureRequested = requested;
        PerformanceSurfaceVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
