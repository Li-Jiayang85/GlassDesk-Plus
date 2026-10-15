using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using GlassDesk.Platform;

namespace GlassDesk.UI;

/// <summary>Recovery signals and bounded metadata; no screen pixels are retained here.</summary>
public static class BackdropRecovery
{
    private static long _generation;
    private static int _basicRendering;
    internal static bool RequiresBasicRendering => Volatile.Read(ref _basicRendering) != 0;
    private static readonly CaptureTraceLog Diagnostics = new();
    internal static long Generation => Interlocked.Read(ref _generation);
    public static void RetryBackground()
    {
        Volatile.Write(ref _basicRendering, 0);
        Interlocked.Increment(ref _generation);
        Record(new("Recovery", "RebuildRequested", Stopwatch.GetTimestamp()));
    }
    public static void RetryBackground(string trigger)
    {
        Record(new("RecoveryTrigger", trigger, Stopwatch.GetTimestamp()));
        RetryBackground();
    }
    public static void UseBasicRendering()
    {
        if (Interlocked.Exchange(ref _basicRendering, 1) != 0) return;
        Interlocked.Increment(ref _generation);
        Record(new("Rendering", "BasicBackgroundRequired", Stopwatch.GetTimestamp()));
    }
    internal static void Record(CaptureTrace trace) => Diagnostics.Observe(trace);
    public static void ExportDiagnosticReport(string path)
    {
        var dwm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dwmapi.dll");
        var report = new
        {
            timestamp = DateTimeOffset.Now, os = WindowsCapabilities.OsVersion.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            applicationVersion = FileVersionInfo.GetVersionInfo(typeof(BackdropRecovery).Assembly.Location).FileVersion,
            dwmVersion = File.Exists(dwm) ? FileVersionInfo.GetVersionInfo(dwm).FileVersion : null,
            recoveryGeneration = Generation, screenPixelsIncluded = false, eventRetentionMinutes = 15,
            note = "Local capture availability and recovery metadata. Counters do not measure displayed FPS.",
            capture = Diagnostics.Snapshot()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
