using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassDeskPlusSetup;

internal static class Program
{
    private const string UpstreamInstaller = "GlassDesk-1.4.4-win-x64-Setup.exe";

    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassDeskPlusPatch", "setup.log");

    [STAThread]
    private static int Main(string[] args)
    {
        DebugLog("Main entry, args=" + string.Join(",", args));

        var silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();

        if (silent)
        {
            try
            {
                DebugLog("silent install start");
                Install(null, silent: true);
                DebugLog("silent install done");
                return 0;
            }
            catch (Exception ex)
            {
                DebugLog("silent install failed: " + ex);
                return 1;
            }
        }

        try
        {
            DebugLog("interactive branch");
            using var form = new InstallerForm();
            Application.Run(form);
            DebugLog("interactive done, exit=" + form.ExitCode);
            return form.ExitCode;
        }
        catch (Exception ex)
        {
            DebugLog("interactive failed: " + ex);
            return 1;
        }
    }

    internal static void Install(IProgress<string>? progress, bool silent)
    {
        void Report(string message)
        {
            DebugLog("step: " + message);
            progress?.Report(message);
        }

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassDeskPlusPatch");
        Directory.CreateDirectory(dataDirectory);

        Report("释放补丁文件…");
        Extract("GlassDeskPatch.dll", Path.Combine(dataDirectory, "GlassDeskPatch.dll"));
        Extract("wechat.jpg", Path.Combine(dataDirectory, "wechat.jpg"));
        Extract("alipay.jpg", Path.Combine(dataDirectory, "alipay.jpg"));

        Report("注册启动钩子…");
        var hookPath = Path.Combine(dataDirectory, "GlassDeskPatch.dll");
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", hookPath, EnvironmentVariableTarget.User);
        BroadcastEnvironmentChange();

        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "GlassDesk");
        var glassDeskExe = Path.Combine(installDirectory, "GlassDesk.exe");

        if (!File.Exists(glassDeskExe))
        {
            Report("安装原版 GlassDesk 1.4.4…");
            var upstream = Extract(UpstreamInstaller);
            var arguments = silent
                ? "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /DIR=\"" + installDirectory + "\""
                : "/NORESTART /SUPPRESSMSGBOXES /DIR=\"" + installDirectory + "\"";
            using var installer = Process.Start(new ProcessStartInfo(upstream, arguments) { UseShellExecute = true });
            installer?.WaitForExit();
        }

        foreach (var process in Process.GetProcessesByName("GlassDesk"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch { }
        }

        if (File.Exists(glassDeskExe))
        {
            Report("启动 GlassDesk…");
            Process.Start(new ProcessStartInfo(glassDeskExe) { UseShellExecute = true });
        }
    }

    private static string Extract(string logicalName, string? destinationPath = null)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException("缺少安装资源：" + logicalName);

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassDeskPlusPatch", "cache");
        Directory.CreateDirectory(cacheDirectory);

        var fileName = destinationPath ?? Path.Combine(cacheDirectory, logicalName);
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
        using var file = File.Create(fileName);
        stream.CopyTo(file);
        return fileName;
    }

    private static void BroadcastEnvironmentChange()
    {
        var result = IntPtr.Zero;
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 2, 5000, out result);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr lpdwResult);

    internal static void DebugLog(string message)
    {
        try
        {
            File.AppendAllText(DebugLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    internal static void TryLog(string message) => DebugLog(message);
}
