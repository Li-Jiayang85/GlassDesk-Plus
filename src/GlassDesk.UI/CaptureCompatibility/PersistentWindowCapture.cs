using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

internal sealed class PersistentWindowCapture : IDisposable
{
    private nint _device,_context,_dxgi,_inspectable;
    private IDirect3DDevice? _projected;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private readonly Action<CaptureTrace>? _trace;
    private readonly WgcWindowCapture.PixelReadback _readback = new();
    private readonly object _notificationGate=new();
    private readonly AutoResetEvent _arrival=new(false);
    private bool _disposed,_notificationsRegistered;
    internal PersistentWindowCapture(nint hwnd,Action<CaptureTrace>? trace=null,nint sharedDevice=0,nint sharedContext=0)
    {
        _trace=trace;
        if((sharedDevice==0)!=(sharedContext==0))
            throw new ArgumentException("A shared capture device requires its immediate context.");
        try
        {
            if(sharedDevice==0)
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(0,1,0,0x20,0,0,7,out _device,out _,out _context));
            else
            {
                // Composition and readback run on the same owning STA. Keep independent
                // COM references so closing the capture cannot invalidate its provider.
                Marshal.AddRef(sharedDevice);_device=sharedDevice;
                Marshal.AddRef(sharedContext);_context=sharedContext;
            }
            _trace?.Invoke(new("GraphicsDevice",sharedDevice==0 ? "Created" : "Shared",System.Diagnostics.Stopwatch.GetTimestamp()));
            var dxgiId=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device,in dxgiId,out _dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(_dxgi,out _inspectable));
            _projected=WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(_inspectable);
            var item=WgcWindowCapture.CreateItem(hwnd);
            _pool=Direct3D11CaptureFramePool.CreateFreeThreaded(_projected,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
            _pool.FrameArrived+=OnFrameArrived;_notificationsRegistered=true;
            _session=_pool.CreateCaptureSession(item);_session.IsCursorCaptureEnabled=false;
            _session.StartCapture();
        }
        catch {Dispose();throw;}
    }
    internal CapturePixels? TryRead(long notBeforeQpcTicks=0,TimeSpan waitForFrame=default,CancellationToken cancellation=default,bool reusePixels=false)
    {
        if(cancellation.IsCancellationRequested) return null;
        _arrival.Reset();
        var frame=ReadOnce(notBeforeQpcTicks,reusePixels);
        if(frame is not null || waitForFrame<=TimeSpan.Zero) return frame;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var handles=cancellation.CanBeCanceled ? new WaitHandle[] {cancellation.WaitHandle,_arrival} : new WaitHandle[] {_arrival};
        while(!cancellation.IsCancellationRequested && timer.Elapsed<waitForFrame)
        {
            _trace?.Invoke(new("NativeWait","Waiting",System.Diagnostics.Stopwatch.GetTimestamp(),BarrierTicks:notBeforeQpcTicks));
            var remaining=waitForFrame-timer.Elapsed;
            if(remaining<=TimeSpan.Zero) break;
            var signaled=WaitHandle.WaitAny(handles,(int)Math.Min(int.MaxValue,Math.Ceiling(remaining.TotalMilliseconds)));
            if(signaled==WaitHandle.WaitTimeout) break;
            if(cancellation.IsCancellationRequested) break;
            frame=ReadOnce(notBeforeQpcTicks,reusePixels);
            if(frame is not null) return frame;
        }
        _trace?.Invoke(new("NativeWait",cancellation.IsCancellationRequested ? "Cancelled" : "Deadline",
            System.Diagnostics.Stopwatch.GetTimestamp(),BarrierTicks:notBeforeQpcTicks));
        return null;
    }
    private CapturePixels? ReadOnce(long notBeforeQpcTicks,bool reusePixels)
    {
        Direct3D11CaptureFrame? latest=null;
        var queued=0;
        try
        {
            // The native pool is a FIFO even though the consumer contract is latest-only.
            // Drain queued textures before readback; release every superseded texture.
            for(var i=0;i<8;i++)
            {
                var next=_pool!.TryGetNextFrame();if(next is null) break;
                queued++;
                if(latest is null || next.SystemRelativeTime>latest.SystemRelativeTime)
                {latest?.Dispose();latest=next;}
                else next.Dispose();
            }
            if(latest is null)
            {_trace?.Invoke(new("NativeQueue","EmptyPool",System.Diagnostics.Stopwatch.GetTimestamp(),BarrierTicks:notBeforeQpcTicks));return null;}
            var sourceTicks=(long)Math.Round(latest.SystemRelativeTime.TotalSeconds*System.Diagnostics.Stopwatch.Frequency);
            if(notBeforeQpcTicks!=0 && latest.SystemRelativeTime.TotalSeconds <
                notBeforeQpcTicks/(double)System.Diagnostics.Stopwatch.Frequency)
            {_trace?.Invoke(new("NativeQueue","BeforeExclusion",System.Diagnostics.Stopwatch.GetTimestamp(),sourceTicks,
                BarrierTicks:notBeforeQpcTicks,QueueFrames:queued));return null;}
            _trace?.Invoke(new("NativeQueue","Delivered",System.Diagnostics.Stopwatch.GetTimestamp(),sourceTicks,
                BarrierTicks:notBeforeQpcTicks,QueueFrames:queued));
            _trace?.Invoke(new("Readback","Starting",System.Diagnostics.Stopwatch.GetTimestamp(),sourceTicks));
            var readStart=System.Diagnostics.Stopwatch.GetTimestamp();
            var pixels=_readback.ReadPixels(_device,_context,latest,_trace,reusePixels);
            _trace?.Invoke(new("Acquisition","ReadbackCompleted",System.Diagnostics.Stopwatch.GetTimestamp(),sourceTicks,EndTicks:readStart));
            return pixels;
        }
        finally {latest?.Dispose();}
    }
    internal void RetireQueuedFramesAtSceneBoundary(long barrierTicks)
    {
        // A queued pre-commit texture can carry a slightly future compositor time.
        // Retire queue ownership at the boundary; source time alone cannot identify it.
        for(var i=0;i<8;i++)
        {
            using var frame=_pool!.TryGetNextFrame();if(frame is null) break;
            var sourceTicks=(long)Math.Round(frame.SystemRelativeTime.TotalSeconds*System.Diagnostics.Stopwatch.Frequency);
            _trace?.Invoke(new("NativeQueue","RetiredAtSceneBoundary",System.Diagnostics.Stopwatch.GetTimestamp(),
                sourceTicks,BarrierTicks:barrierTicks));
        }
    }
    // Called only by the owning capture worker, between readbacks. Retain the
    // native session and frame pool so the next reveal needs no capture startup.
    internal void ReleaseReadbackBuffers() => _readback.Dispose();
    public void Dispose()
    {
        lock(_notificationGate)
        {
            if(_disposed) return;
            _disposed=true;
        }
        if(_pool is not null && _notificationsRegistered) {_pool.FrameArrived-=OnFrameArrived;_notificationsRegistered=false;}
        _session?.Dispose();_session=null;_pool?.Dispose();_pool=null;
        _projected?.Dispose();_projected=null;
        _readback.Dispose();
        Release(ref _inspectable);Release(ref _dxgi);Release(ref _context);Release(ref _device);
        lock(_notificationGate) _arrival.Dispose();
    }
    private void OnFrameArrived(Direct3D11CaptureFramePool sender,object args)
    {
        lock(_notificationGate) {if(_disposed) return;_arrival.Set();}
        _trace?.Invoke(new("FrameArrival","Arrived",System.Diagnostics.Stopwatch.GetTimestamp()));
    }
    private static void Release(ref nint p) {if(p!=0) Marshal.Release(p);p=0;}
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,uint type,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device,out nint inspectable);
}
