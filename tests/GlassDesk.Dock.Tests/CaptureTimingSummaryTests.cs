using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace GlassDesk.Dock.Tests;

public sealed class CaptureTimingSummaryTests
{
    [Fact]
    public void StageCostsRemainSummarizedAfterTheRecentEventQueueRollsOver()
    {
        var log = new CaptureTraceLog();
        var now = Stopwatch.GetTimestamp();
        log.Observe(new("CaptureCost", "SceneUpdate", now, EndTicks: now - Stopwatch.Frequency / 1000));
        log.Observe(new("CaptureCost", "SceneUpdate", now, EndTicks: now - Stopwatch.Frequency / 500));
        for (var i = 0; i < 300; i++) log.Observe(new("Geometry", "Changed", now));
        var summary = log.Snapshot();
        Assert.NotNull(summary.GetType().GetProperty("Timings", BindingFlags.Instance | BindingFlags.Public));
        var json = JsonSerializer.SerializeToElement(summary);
        var timing = json.GetProperty("Timings").EnumerateArray().Single();
        Assert.Equal("SceneUpdate", timing.GetProperty("Reason").GetString());
        Assert.Equal(2, timing.GetProperty("Count").GetInt32());
        Assert.InRange(timing.GetProperty("MeanMilliseconds").GetDouble(), 1.49, 1.51);
        Assert.InRange(timing.GetProperty("MaximumMilliseconds").GetDouble(), 1.99, 2.01);
    }
}
