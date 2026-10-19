using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

/// <summary>
/// 这份 Windows 安装能做什么。
/// </summary>
/// <remarks>
/// 只探测一次并缓存。每个依赖特定 Windows 版本的功能
/// 都在这里询问，而不是就地判断版本，
/// 这样不支持的组合会一致地报告自身状态，
/// 「不支持的版本如实说明而不是猜测」这条规则也只需在一个地方生效。
///
/// 版本取自 <c>RtlGetVersion</c> 而不是 <see cref="Environment.OSVersion"/>，
/// 因为后者可能被应用程序兼容性 shim 改写，
/// 而这些检查的意义恰恰在于确认版本号。
/// </remarks>
public static partial class WindowsCapabilities
{
    /// <summary>Windows 11 首个版本。圆角窗口开始可用。</summary>
    private const int Windows11Build = 22000;

    /// <summary>Windows 11 22H2。<c>DWMWA_SYSTEMBACKDROP_TYPE</c> 开始可用。</summary>
    private const int SystemBackdropBuild = 22621;

    /// <summary>官方支持深色模式窗口特性的起始版本。</summary>
    private const int ImmersiveDarkModeBuild = 18985;

    /// <summary>
    /// 产品最低系统门槛：Windows 10 版本 2004（Build 19041）。
    /// </summary>
    /// <remarks>
    /// 选 19041 而不是更低的版本，是因为 <c>WDA_EXCLUDEFROMCAPTURE</c>
    /// （截图排除，见 <c>CaptureExclusionSession</c>）从 2004 起才可用；
    /// 再往下虽然程序能跑，但“Dock 不出现在截图里”会静默失效。
    /// </remarks>
    private const int MinimumSupportedBuild = 19041;

    static WindowsCapabilities()
    {
        var (major, minor, build) = ReadVersion();
        OsVersion = new Version(major, minor, build);
        BuildNumber = build;
    }

    /// <summary>运行中操作系统的真实版本，例如 10.0.26100。</summary>
    public static Version OsVersion { get; private set; }

    public static int BuildNumber { get; private set; }

    /// <summary>
    /// 把版本号替换成指定构建，用于在 Windows 11 上验证 Windows 10 的降级路径。
    /// </summary>
    /// <remarks>
    /// 只给自检/验收（<c>--verify-os-build</c>）使用：不改变任何系统状态，
    /// 只是让 <see cref="SupportsSystemBackdrop"/> 这类能力探测按旧构建回答，
    /// 从而确认降级路径真的能跑起来。传 0 恢复真实版本。
    /// </remarks>
    public static void OverrideBuildForVerification(int build)
    {
        if (build <= 0)
        {
            var (major, minor, real) = ReadVersion();
            OsVersion = new Version(major, minor, real);
            BuildNumber = real;
            return;
        }

        BuildNumber = build;
        OsVersion = new Version(10, 0, build);
    }

    /// <summary>产品最低系统门槛；未来 Build 不设置上限。</summary>
    public static bool IsSupported => IsProductBuildSupported(BuildNumber);
    public static bool IsProductBuildSupported(int build) => build >= MinimumSupportedBuild;

    public static bool IsWindows11OrGreater => BuildNumber >= Windows11Build;

    /// <summary>处在 Windows 10 支持区间（2004 及以上，但还没到 Windows 11）。</summary>
    public static bool IsWindows10 => !IsWindows11OrGreater && IsSupported;

    /// <summary>
    /// DWM 是否会响应背景材质请求。
    /// 为 false 时窗口必须自行绘制半透明面板。
    /// </summary>
    public static bool SupportsSystemBackdrop => BuildNumber >= SystemBackdropBuild;

    /// <summary>能否向 DWM 请求窗口圆角。</summary>
    public static bool SupportsWindowCornerPreference => BuildNumber >= Windows11Build;

    /// <summary>非客户区能否跟随应用主题。</summary>
    public static bool SupportsImmersiveDarkMode => BuildNumber >= ImmersiveDarkModeBuild;

    /// <summary>
    /// 供设置窗口的「关于」页和诊断使用的可读摘要。
    /// </summary>
    public static string Describe()
    {
        var backdrop = SupportsSystemBackdrop ? "支持系统背景材质" : "不支持系统背景材质（使用自绘面板）";
        var generation = IsWindows11OrGreater ? "Windows 11" : IsWindows10 ? "Windows 10（降级外观）" : "不受支持的版本";
        return $"Windows {OsVersion}（Build {BuildNumber}）— {generation}，{backdrop}";
    }

    private static (int Major, int Minor, int Build) ReadVersion()
    {
        try
        {
            var info = new RtlOsVersionInfo
            {
                Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>(),
            };

            // 在所有受支持的 Windows 上都返回 STATUS_SUCCESS (0)。
            // 返回值非零表示调用本身失败，
            // 此时托管的回退值仍好过报告一个荒唐的版本号。
            if (NtDll.RtlGetVersion(ref info) == 0)
            {
                return ((int)info.MajorVersion, (int)info.MinorVersion, (int)info.BuildNumber);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 继续走托管的回退值。
        }

        var fallback = Environment.OSVersion.Version;
        return (fallback.Major, fallback.Minor, fallback.Build);
    }

    /// <summary>
    /// 对应 <c>RTL_OSVERSIONINFOW</c>。固定长度的字符缓冲区让结构体保持可直接封送，
    /// 这也正是下面能使用源生成封送的原因。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct RtlOsVersionInfo
    {
        public uint Size;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public fixed char ServicePack[128];
    }

    private static partial class NtDll
    {
        [LibraryImport("ntdll.dll")]
        internal static partial int RtlGetVersion(ref RtlOsVersionInfo versionInfo);
    }
}
