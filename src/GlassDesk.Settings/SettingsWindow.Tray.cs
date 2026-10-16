using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Settings;

/// <summary>设置中心的「托盘图标」小节：显示开关，以及从已有自定义图标里挑一张。</summary>
/// <remarks>
/// 图标来源刻意限制在数据目录 <c>icons/custom-*</c> 里已有的文件。这样不必再实现一套
/// 「把用户选的文件复制进数据目录」的逻辑，也自动继承了应用库那套自定义图标的生命周期：
/// 备份会带走它们，条目删除时清掉的图标也会同步从这份列表里消失。
/// 列表为空时下拉框只留「内置图标」一项，不会出现一个点不动的空控件。
/// </remarks>
public partial class SettingsWindow
{
    /// <summary>托盘设置已保存并落盘。</summary>
    public event EventHandler? TraySettingsChanged;

    /// <summary>下拉框中「内置图标」那一项的标记值。</summary>
    private const string BuiltInIconOption = "";

    private bool _loadingTrayControls;
    private string? _trayIconPreviewPath;

    private void LoadTrayControls()
    {
        _loadingTrayControls = true;
        try
        {
            var tray = _dataStore?.Settings.Current.Tray;
            TrayShowIconCheck.IsChecked = tray?.ShowIcon ?? true;
            TrayShowIconCheck.IsEnabled = _dataStore is not null;

            BuildTrayIconOptions(tray?.CustomIconFileName);
            UpdateTrayIconPreview();

            TrayIconCombo.IsEnabled = _dataStore is not null && TrayIconCombo.Items.Count > 1;
            TraySettingsStatus.Text = _dataStore is null
                ? "数据尚未就绪。"
                : TrayIconCombo.Items.Count > 1
                    ? "修改后立即保存并应用到通知区域。"
                    : "暂无自定义图标：先在启动台里给某个应用换一张图标，它就会出现在这里。";
        }
        finally { _loadingTrayControls = false; }
    }

    /// <summary>用数据目录里现有的自定义图标重建下拉项，并选中当前生效的那一张。</summary>
    private void BuildTrayIconOptions(string? selected)
    {
        TrayIconCombo.Items.Clear();
        TrayIconCombo.Items.Add(new ComboBoxItem { Content = "内置图标", Tag = BuiltInIconOption });

        var names = _dataStore?.Paths.ManagedCustomIconFileNames() ?? [];
        foreach (var name in names)
            TrayIconCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        var index = 0;
        for (var i = 0; i < TrayIconCombo.Items.Count; i++)
        {
            if (TrayIconCombo.Items[i] is ComboBoxItem item
                && item.Tag is string tag && tag == (selected ?? BuiltInIconOption))
            {
                index = i;
                break;
            }
        }
        TrayIconCombo.SelectedIndex = index;
    }

    private void OnTrayControlChanged(object sender, RoutedEventArgs e) => SaveTraySettings();

    private void OnTrayIconSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTrayControls) return;
        UpdateTrayIconPreview();
        SaveTraySettings();
    }

    private void SaveTraySettings()
    {
        if (_loadingTrayControls || _dataStore is null) return;

        var current = _dataStore.Settings.Current;
        var previous = current.Tray ?? new TraySettings();
        var next = new TraySettings
        {
            ShowIcon = TrayShowIconCheck.IsChecked == true,
            CustomIconFileName = SelectedTrayIconName(),
        };

        if (previous.ShowIcon == next.ShowIcon
            && string.Equals(previous.CustomIconFileName, next.CustomIconFileName, StringComparison.Ordinal))
        {
            return;
        }

        // 只有文件确实存在才接受这个选择：settings.json 可以被手工编辑。
        if (next.CustomIconFileName is { Length: > 0 }
            && _dataStore.Paths.ResolveManagedCustomIcon(next.CustomIconFileName) is null)
        {
            next.CustomIconFileName = null;
            TraySettingsStatus.Text = "所选图标文件已不存在，已回到内置图标。";
        }

        var candidate = SettingsPageEditor.Clone(current);
        candidate.Tray = next;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null)
        {
            LoadTrayControls();
            TraySettingsStatus.Text = "设置无效，未保存：" + validationError;
            return;
        }

        try
        {
            current.Tray = next;
            _dataStore.Settings.Save();
        }
        catch (Exception ex)
        {
            current.Tray = previous;
            LoadTrayControls();
            TraySettingsStatus.Text = "保存失败，已恢复原设置：" + ex.Message;
            return;
        }

        TraySettingsChanged?.Invoke(this, EventArgs.Empty);
        TraySettingsStatus.Text = next.ShowIcon ? "已保存并应用。" : "已保存：通知区域不再显示图标。";
    }

    private string? SelectedTrayIconName()
        => TrayIconCombo.SelectedItem is ComboBoxItem { Tag: string tag } && tag.Length > 0 ? tag : null;

    /// <summary>预览当前选中的图标；内置图标沿用品牌标识，自定义图标直接读文件。</summary>
    private void UpdateTrayIconPreview()
    {
        var name = SelectedTrayIconName();
        var path = name is null ? null : _dataStore?.Paths.ResolveManagedCustomIcon(name);
        if (path is not null && path == _trayIconPreviewPath && TrayIconPreview.Source is not null) return;
        _trayIconPreviewPath = path;

        try
        {
            TrayIconPreview.Source = path is null ? null : DecodeIconPreview(path);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            TrayIconPreview.Source = null;
            TraySettingsStatus.Text = "这个图标文件无法显示：" + ex.Message;
        }

        // 内置标识取自窗口资源，和托盘用的那份是同一个画法。
        if (path is null)
            TrayIconPreview.Source = TryFindResource("GlassDeskBrandMark") as ImageSource;
    }

    private static BitmapImage DecodeIconPreview(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.DecodePixelHeight = 64;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
