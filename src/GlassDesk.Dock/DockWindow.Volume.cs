using System.Runtime.InteropServices;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    /// <summary>
    /// 读取系统主音量（0~1）。失败返回 false。
    /// </summary>
    /// <remarks>
    /// 用 Core Audio 的 <c>IAudioEndpointVolume</c>：滑杆必须能"读回当前音量"，
    /// 而音量键只能单向发指令、读不到值，所以滑杆这一版必须走 COM。
    /// </remarks>
    private static bool TryGetMasterVolume(out float level)
    {
        level = 0;
        try
        {
            var endpoint = GetEndpointVolume();
            if (endpoint is null)
            {
                return false;
            }

            var hr = endpoint.GetMasterVolumeLevelScalar(out level);
            Marshal.ReleaseComObject(endpoint);
            return hr == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>设置系统主音量（0~1）。失败返回 false。</summary>
    private static bool TrySetMasterVolume(float level)
    {
        try
        {
            var endpoint = GetEndpointVolume();
            if (endpoint is null)
            {
                return false;
            }

            var context = Guid.Empty;
            var hr = endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref context);
            Marshal.ReleaseComObject(endpoint);
            return hr == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>取默认播放设备的主音量接口；任何一步失败都返回 null（调用方静默降级）。</summary>
    private static IAudioEndpointVolume? GetEndpointVolume()
    {
        // 用 CLSID 直接创建实例，而不是声明 coclass：后者需要额外的互操作声明，
        // 且私有 sealed 类上的隐式转换不成立（CS0030）。这里静态类型是 object，
        // 转换在运行期由 COM QueryInterface 完成。
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        if (type is null || Activator.CreateInstance(type) is not IMMDeviceEnumerator enumerator)
        {
            return null;
        }

        try
        {
            // eRender = 0（播放设备），eMultimedia = 1（多媒体角色，与系统音量面板一致）。
            var hr = enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
            if (hr != 0 || device is null)
            {
                return null;
            }

            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                // CLSCTX_ALL = 23
                hr = device.Activate(ref iid, 23, IntPtr.Zero, out var instance);
                return hr == 0 ? instance as IAudioEndpointVolume : null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // 只声明用得到的前几个方法，顺序必须与真实 vtable 一致。
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        int GetDevice(string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid interfaceId, int classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        int OpenPropertyStore(int access, out IntPtr properties);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);

        int UnregisterControlChangeNotify(IntPtr notify);

        int GetChannelCount(out int count);

        int SetMasterVolumeLevel(float level, ref Guid context);

        int SetMasterVolumeLevelScalar(float level, ref Guid context);

        int GetMasterVolumeLevel(out float level);

        int GetMasterVolumeLevelScalar(out float level);
    }
}
