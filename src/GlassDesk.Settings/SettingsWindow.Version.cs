using System.Reflection;
using System.Windows;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    /// <summary>
    /// 显示当前产品版本。
    /// </summary>
    /// <remarks>
    /// 本分支已移除「检查更新」功能：原来的更新通道指向上游作者的发布仓库，
    /// 用它更新会把本分支自己的功能覆盖掉，所以连同入口、状态行与自动检查一起去掉，
    /// 「关于」页只保留版本显示。
    /// </remarks>
    public void ShowProductVersion()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];

        ProductVersionText.Text = "GlassDesk " + (string.IsNullOrWhiteSpace(version) ? "（未知版本）" : version);
    }
}
