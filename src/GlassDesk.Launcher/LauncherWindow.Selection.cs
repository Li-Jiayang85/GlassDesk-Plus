using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Core.Models;

namespace GlassDesk.Launcher;

/// <summary>
/// 启动台的批量删除：右键任意图标进入多选，靠勾选角标确认选中，顶部「删除」一次移除。
/// </summary>
/// <remarks>
/// <b>不用复选框，只看勾选角标。</b>角标是唯一的选中指示，所以它必须一眼可辨——
/// 单击磁贴切换勾选，勾上的就是会被删掉的。
///
/// <b>顶部按钮：多选时「添加」旁边多出「取消选择」与「删除」</b>，三个按钮同一行：
/// 搜索框 | 取消选择 | 添加 | 删除。普通模式下只留「添加」。删除是破坏性动作，
/// 用红色胶囊与其余按钮区分开。不再有底部工具条；Del 键在多选期间不做任何事，
/// 删除只由顶部那个按钮发起，避免误触。
///
/// 删除语义与单个「删除」完全一致：只把条目移出启动台布局，源文件和应用库记录都保留，
/// 之后随时可以重新添加，所以不做二次确认，只用状态条提示移除了几个。
///
/// 多选期间必须屏蔽图标拖拽：长按拾取用的是同一条鼠标路径，若不禁用，用户点选就成了拖拽。
/// 屏蔽点在 <c>OnTileMouseDown</c>，那里是拾取手势的唯一入口。
/// </remarks>
public partial class LauncherWindow
{
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);

    /// <summary>多选模式是否开启。以显式状态为准，不依赖任何控件的可见性。</summary>
    private bool IsSelecting { get; set; }

    /// <summary>进入多选模式，并把触发它的那个条目选上。</summary>
    private void BeginSelection(string triggerId)
    {
        if (_view.IsSearching)
        {
            ShowNotice("搜索结果中不能批量删除；请先清空搜索。");
            return;
        }

        CancelDrag();
        _view.OpenGroupId = null;
        GroupPanel.Visibility = Visibility.Collapsed;
        _selectedIds.Clear();
        _selectedIds.Add(triggerId);
        IsSelecting = true;
        RefreshSelection();
    }

    private void EndSelection()
    {
        _selectedIds.Clear();
        IsSelecting = false;
        RefreshSelection();
    }

    private void ToggleSelection(string id)
    {
        if (!_selectedIds.Remove(id)) _selectedIds.Add(id);
        RefreshSelection();
    }

    /// <summary>把选中状态写回磁贴角标与顶部按钮，不重建元素树——多选是要反复点的动作。</summary>
    private void RefreshSelection()
    {
        // 布局可能已被其它路径改过，先丢掉已经不存在的选中项。
        var live = CurrentSlots().Select(SlotId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        _selectedIds.RemoveWhere(id => !live.Contains(id!));

        var selecting = IsSelecting;
        foreach (var button in TileGrid.Children.OfType<LauncherAppTileButton>())
            ApplySelectionVisual(button, selecting);

        // 顶部按钮：多选时在「添加」旁边多出「取消选择」与「删除」，「添加」始终保留。
        SelectionCancelButton.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        SelectionDeleteButton.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        SelectionDeleteButton.Content = _selectedIds.Count > 1 ? $"删除 {_selectedIds.Count} 项" : "删除";
        SelectionDeleteButton.IsEnabled = _selectedIds.Count > 0;
    }

    /// <summary>选中态由依赖属性驱动角标显隐，这里只补外框与底色。</summary>
    private void ApplySelectionVisual(LauncherAppTileButton button, bool selecting)
    {
        var selected = selecting && button.Tag is string id && _selectedIds.Contains(id);
        button.IsSelected = selected;
        button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)) : Brushes.Transparent;
        button.BorderThickness = new Thickness(selected ? 2 : 1);
        button.Background = selected ? new SolidColorBrush(Color.FromArgb(0x2E, 0x0A, 0x84, 0xFF)) : Brushes.Transparent;
    }

    private static string? SlotId(LauncherSlot slot) => slot.Kind == LauncherSlotKind.Group ? slot.Group?.Id : slot.ItemId;

    private void OnSelectionCancel(object sender, RoutedEventArgs e) => EndSelection();

    private void OnSelectionDelete(object sender, RoutedEventArgs e) => CommitSelectionDelete();

    private void CommitSelectionDelete()
    {
        if (_selectedIds.Count == 0) return;
        if (_workspace is null || _view.IsSearching)
        {
            ShowNotice("搜索结果中不能批量删除；请先清空搜索。");
            return;
        }

        // 选中项可能横跨多页，全部一起删；找不到的 id 由编辑器忽略。
        var targets = _selectedIds.ToArray();
        var before = _workspace.Layout.Pages.SelectMany(page => page.Slots).Count();
        CommitEdit(layout => LauncherLayoutEditor.RemoveShortcuts(layout, targets, _view.Capacity));
        var after = _workspace.Layout.Pages.SelectMany(page => page.Slots).Count();
        var removed = before - after;

        EndSelection();
        if (removed > 0)
            ShowNotice($"已从启动台移除 {removed} 个图标，需要时可重新添加。", autoDismiss: true);
    }

    /// <summary>多选期间只接管 Esc（退出多选）。Del 不删东西，避免误触。</summary>
    private void OnLauncherPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSelecting) return;
        if (e.Key == Key.Escape)
        {
            EndSelection();
            e.Handled = true;
        }
    }
}
