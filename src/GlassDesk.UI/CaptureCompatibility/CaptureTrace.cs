internal readonly record struct CaptureTrace(string Stage,string Reason,long AtTicks,
    long SourceTicks=0,long EndTicks=0,long BarrierTicks=0,long Generation=0,int QueueFrames=0,
    long WindowHandle=0,uint WindowProcess=0,string? WindowClass=null);

internal sealed class CaptureTraceLog
{
    private readonly object _gate=new();
    private readonly Dictionary<(string Stage,string Reason),int> _counts=new();
    private readonly HashSet<long> _nativeSources=new(),_consumerSources=new();
    private readonly Queue<CaptureTrace> _events=new();
    private readonly Queue<long> _arrivals=new();
    private readonly Dictionary<(string Stage,string Reason),(int Count,double Total,double Maximum)> _timings=new();
    private int _nativeAcquisitions,_consumerReads,_consumerUnavailable,_repeatedConsumerReads;
    private bool _sourceLimitReached;
    internal void Observe(CaptureTrace trace)
    {
        lock(_gate)
        {
            var key=(trace.Stage,trace.Reason);_counts[key]=_counts.GetValueOrDefault(key)+1;
            if (trace.Stage is "CaptureCost" or "Processing" && trace.EndTicks > 0 && trace.AtTicks >= trace.EndTicks)
            {
                var milliseconds=(trace.AtTicks-trace.EndTicks)*1000d/System.Diagnostics.Stopwatch.Frequency;
                var previous=_timings.GetValueOrDefault(key);
                _timings[key]=(previous.Count+1,previous.Total+milliseconds,Math.Max(previous.Maximum,milliseconds));
            }
            if(trace.Stage=="Scene" && trace.Reason=="Delivered")
            {_nativeAcquisitions++;Track(_nativeSources,trace.SourceTicks);}
            if(trace.Stage=="Consumer")
            {
                if(trace.Reason=="Delivered")
                {
                    _consumerReads++;
                    if(_consumerSources.Contains(trace.SourceTicks)) _repeatedConsumerReads++;
                    Track(_consumerSources,trace.SourceTicks);
                }
                else _consumerUnavailable++;
            }
            if(trace.Stage=="FrameArrival")
            {
                if(_arrivals.Count==1024) _arrivals.Dequeue();
                _arrivals.Enqueue(trace.AtTicks);
            }
            else if(trace.Reason!="Delivered" && trace.Stage!="Readback")
            {
                if(_events.Count==256) _events.Dequeue();
                _events.Enqueue(trace);
            }
        }
    }
    private void Track(HashSet<long> sources,long ticks)
    {
        if(ticks==0 || sources.Contains(ticks)) return;
        if(sources.Count<8192) sources.Add(ticks);else _sourceLimitReached=true;
    }
    internal CaptureTraceSummary Snapshot()
    {
        lock(_gate)
        {
        var earliest = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 60 * 15;
        while (_events.Count > 0 && _events.Peek().AtTicks < earliest) _events.Dequeue();
        while (_arrivals.Count > 0 && _arrivals.Peek() < earliest) _arrivals.Dequeue();
        return new(_counts.OrderBy(x=>x.Key.Stage).ThenBy(x=>x.Key.Reason)
            .Select(x=>new CaptureTraceCount(x.Key.Stage,x.Key.Reason,x.Value)).ToArray(),
            _nativeAcquisitions,_nativeSources.Count,_consumerReads,_consumerSources.Count,_repeatedConsumerReads,
            _consumerUnavailable,_sourceLimitReached,_events.ToArray(),_arrivals.ToArray(),
            _timings.OrderBy(x=>x.Key.Stage).ThenBy(x=>x.Key.Reason).Select(x=>new CaptureTimingCount(
                x.Key.Stage,x.Key.Reason,x.Value.Count,x.Value.Total/x.Value.Count,x.Value.Maximum)).ToArray());
        }
    }
}
internal sealed record CaptureTraceCount(string Stage,string Reason,int Count);
internal sealed record CaptureTimingCount(string Stage,string Reason,int Count,double MeanMilliseconds,double MaximumMilliseconds);
internal sealed record CaptureTraceSummary(CaptureTraceCount[] Counts,int NativeAcquisitions,int DistinctNativeSources,
    int ConsumerReads,int DistinctConsumerSources,int RepeatedConsumerReads,int ConsumerUnavailable,
    bool SourceLimitReached,CaptureTrace[] RecentEvents,long[] RecentArrivalTicks,CaptureTimingCount[] Timings);
