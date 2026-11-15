using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GlassDesk.Platform;

/// <summary>
/// 打开 Windows 的系统透明效果（设置 → 个性化 → 颜色 → 透明效果）。
/// </summary>
/// <remarks>
/// 玻璃面板最终由桌面窗口管理器合成。用户在系统里关掉「透明效果」后，
/// 半透明/亚克力请求会被 DWM 当成不透明处理，于是同一份代码在部分机型上
/// 看起来完全没有玻璃感——这不是 GlassDesk 自己的开关能修好的。
/// 安装程序在拷贝完文件后调用这里，把系统开关打开，并广播一次主题变化，
/// 让 DWM 立刻重新读取，而不必注销重登。
/// </remarks>
public static class SystemTransparency
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string TransparencyValueName = "EnableTransparency";

    private const uint WmSettingChange = 0x001A;
    private const uint WmThemeChanged = 0x031A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint BroadcastTimeoutMs = 1500;

    private static readonly nint HwndBroadcast = unchecked((nint)0xFFFF);

    /// <summary>开关操作的结果，供安装程序与命令行走廊判断退出码。</summary>
    public readonly record struct Result(bool WasEnabled, bool Changed, string? Error)
    {
        /// <summary>写到注册表并通知系统都成功。</summary>
        public bool Applied => Error is null;

        public string Describe() => Error is not null
            ? "打开系统透明效果失败：" + Error
            : WasEnabled
                ? "系统透明效果本来就是开启的，无需改动。"
                : "已打开系统透明效果，并已通知桌面窗口管理器立即生效。";
    }

    /// <summary>
    /// 确保系统透明效果处于开启状态。已经开启时不写注册表，也不打扰系统。
    /// </summary>
    public static Result Enable()
    {
        try
        {
            var wasEnabled = IsEnabled();
            if (!wasEnabled) WriteEnabled();
            if (!wasEnabled) NotifyThemeChanged();
            return new Result(wasEnabled, !wasEnabled, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or InvalidOperationException)
        {
            return new Result(false, false, ex.Message);
        }
    }

    /// <summary>当前用户是否打开了系统透明效果；值缺失按「开启」处理，与全新安装一致。</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
        var raw = key?.GetValue(TransparencyValueName);
        return raw switch
        {
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => true,
        };
    }

    private static void WriteEnabled()
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的个性化注册表项。");
        key.SetValue(TransparencyValueName, 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 广播主题变化。Explorer 与 DWM 收到 <c>WM_SETTINGCHANGE("ImmersiveColorSet")</c>
    /// 会重新读取个性化设置，于是新值立刻生效。
    /// </summary>
    private static void NotifyThemeChanged()
    {
        _ = SendMessageTimeoutText(HwndBroadcast, WmSettingChange, 0, "ImmersiveColorSet",
            SmtoAbortIfHung, BroadcastTimeoutMs, out _);
        _ = SendMessageTimeoutValue(HwndBroadcast, WmThemeChanged, 0, 0,
            SmtoAbortIfHung, BroadcastTimeoutMs, out _);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutText(nint hWnd, uint msg, nint wParam, string lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeoutValue(nint hWnd, uint msg, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);
}
