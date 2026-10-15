using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

/// <summary>The ordered visible desktop scene, excluding our own glass and popup HWNDs.</summary>
internal sealed class GlassBackdropScene : IEquatable<GlassBackdropScene>
{
    internal readonly record struct SceneWindow(nint Handle, uint ProcessId, int Left, int Top, int Right, int Bottom,
        bool IsTransientOverlay = false);
    private readonly SceneWindow[] _windows;
    private readonly long _recoveryGeneration = GlassDesk.UI.BackdropRecovery.Generation;

    internal GlassBackdropScene(params SceneWindow[] windows) => _windows = windows.ToArray();

    internal static GlassBackdropScene? Capture() => Capture(null);

    internal static GlassBackdropScene? Capture(PixelRect? region)
    {
        var windows = new List<SceneWindow>();
        var complete = true;
        var family = CaptureProcessFamily.KnownProcesses;
        var enumerated = EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            if (GetWindowThreadProcessId(handle, out var process) == 0) { complete = false; return true; }
            if (process == Environment.ProcessId || family.Contains((int)process)) return true;
            if (!GetWindowRect(handle, out var rect)) { complete = false; return true; }
            // Off-screen carriers and windows on other monitors cannot contribute
            // pixels to this capture. Their movement must not erase its material.
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            if (region is { } area && (rect.Right <= area.X || rect.Bottom <= area.Y
                || rect.Left >= area.Right || rect.Top >= area.Bottom)) return true;
            // Layered, pointer-transparent tool windows (for example driver HUDs)
            // are transient overlays, not ordinary application content windows.
            var transientOverlay = (GetWindowLongPtr(handle, -20).ToInt64() & 0x800a0) == 0x800a0;
            windows.Add(new(handle, process, rect.Left, rect.Top, rect.Right, rect.Bottom, transientOverlay));
            return true;
        }, 0);
        return enumerated && complete ? new(windows.ToArray()) : null;
    }

    internal bool CanRetainMaterialFrom(GlassBackdropScene? previous) => previous is not null
        && _recoveryGeneration == previous._recoveryGeneration
        && previous._windows.All(window => window.IsTransientOverlay || _windows.Contains(window));

    // A visible surface may finish the currently displayed texture while a new
    // ordinary window scene is acquired. Recovery boundaries remain invalid.
    internal bool CanHandoffMaterialFrom(GlassBackdropScene? previous) => previous is not null
        && _recoveryGeneration == previous._recoveryGeneration;

    public bool Equals(GlassBackdropScene? other) => other is not null && _recoveryGeneration == other._recoveryGeneration
        && _windows.AsSpan().SequenceEqual(other._windows);
    public override bool Equals(object? obj) => obj is GlassBackdropScene other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_recoveryGeneration);
        foreach (var window in _windows) hash.Add(window);
        return hash.ToHashCode();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
}
