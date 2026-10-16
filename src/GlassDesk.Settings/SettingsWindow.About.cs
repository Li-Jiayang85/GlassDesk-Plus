using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Navigation;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    /// <summary>
    /// 打开原作者的项目页面。
    /// </summary>
    /// <remarks>
    /// 显式处理而不是依赖 <see cref="System.Windows.Documents.Hyperlink"/> 的默认跳转：
    /// 用系统默认浏览器打开更可控，失败也只在设置页里静默吞掉——点不开一个链接
    /// 不该让整个设置中心出错。
    /// </remarks>
    private void OnOriginalAuthorLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }

        e.Handled = true;
    }
}
