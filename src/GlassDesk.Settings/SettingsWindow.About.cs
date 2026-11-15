using System.Diagnostics;
using System.Reflection;
using System.Windows.Navigation;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    /// <summary>把运行中的产品版本显示在“关于”页。</summary>
    /// <remarks>
    /// 宿主在绑定窗口时把打包写入的版本传进来；单独打开设置中心
    /// （例如验收工具）时退回可执行文件自身的程序集版本。
    /// </remarks>
    public void ShowProductVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        ProductVersionText.Text = "GlassDesk " + version;
    }

    /// <summary>宿主尚未传入版本时，“关于”页显示的程序集版本。</summary>
    internal static Version DefaultProductVersion =>
        (Assembly.GetEntryAssembly() ?? typeof(SettingsWindow).Assembly).GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>“关于”页里的官网链接走系统默认浏览器。</summary>
    private void OnOpenHomepageNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 没有默认浏览器时不要把设置中心一起拖垮。
        }
    }
}
