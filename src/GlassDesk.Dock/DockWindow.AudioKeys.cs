using System.Runtime.InteropServices;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    /// <summary>音量降低键。</summary>
    private const byte VolumeDownKey = 0xAE;

    /// <summary>音量升高键。</summary>
    private const byte VolumeUpKey = 0xAF;

    /// <summary>静音键。</summary>
    private const byte VolumeMuteKey = 0xAD;

    /// <summary>发送一次系统音量键（按下 + 抬起）。</summary>
    /// <remarks>
    /// 这里刻意用系统音量键，而不是 Core Audio 的 <c>IAudioEndpointVolume</c>：
    /// 后者要写一百来行 COM 互操作，而"几个按键就够用"的需求下不值得。
    /// 音量键由系统统一处理，对所有播放器生效，也不需要任何权限或 API key。
    /// 发送失败（极少见）只吞掉：点一下音量键不该影响任何其它功能。
    /// </remarks>
    private static void TapVolumeKey(byte virtualKey)
    {
        try
        {
            VolumeKeyInterop.KeybdEvent(virtualKey, 0, 0, 0);
            VolumeKeyInterop.KeybdEvent(virtualKey, 0, KeyEventKeyUp, 0);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>抬起标志（KEYEVENTF_KEYUP）。</summary>
    private const uint KeyEventKeyUp = 0x0002;

    private static partial class VolumeKeyInterop
    {
        [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
        internal static partial void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nint extraInfo);
    }
}
