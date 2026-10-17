using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

/// <summary>
/// 启动台批量删除的现场验收：真实 LauncherWindow、真实多选与提交路径，数据目录是临时的。
/// </summary>
/// <remarks>
/// 多选入口与删除动作都是窗口上的私有成员，这里用反射调用它们——比在验收工程里
/// 复制一份逻辑可信得多：跑的就是产品代码本身。鼠标手势（右键弹菜单、点图标）不在
/// 覆盖范围内，那部分需要真机点击走查。
/// </remarks>
internal static partial class Program
{
    private static int VerifyBatchDelete(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "batch-delete.json"));
        var supplied = Argument(args, "--data-dir");
        var root = supplied is null ? Path.Combine(Path.GetTempPath(), "GlassDesk-BatchDelete-" + Guid.NewGuid().ToString("n")) : Path.GetFullPath(supplied);
        var real = AppPaths.ForCurrentUser().RootDirectory;
        if (root.Equals(real, StringComparison.OrdinalIgnoreCase) || root.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to use the user's GlassDesk data directory.");
            return 2;
        }

        Directory.CreateDirectory(root);
        LauncherWindow? window = null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml", UriKind.Absolute)
        });
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
            for (var i = 0; i < 36; i++)
                data.Library.Add(new AppEntry { Id = $"wpf-{i:00}", DisplayName = $"App {i:00}", Target = Path.Combine(root, $"missing-{i}"), IsTargetMissing = true });
            data.Library.Save();

            using var host = new AppServiceHost(data);
            var primarySession = new PrimaryDisplaySession(DisplayService.Primary()
                ?? throw new InvalidOperationException("Windows Primary Monitor is unavailable."));
            window = new LauncherWindow();
            window.BindPrimaryDisplay(primarySession);
            window.AttachServices(host, true);
            window.ApplySettings(data.Settings.Current.Launcher);
            window.Show();
            Pump(TimeSpan.FromMilliseconds(200));

            // 固定从第 1 页起步：否则上一次的状态可能让断言读到第 2 页的磁贴（曾偶发过）。
            var pageText = (TextBlock)window.FindName("PageText")!;
            for (var guard = 0; guard < 8 && !pageText.Text.StartsWith("1 /", StringComparison.Ordinal); guard++)
            {
                window.NavigatePage(-1);
                Pump(TimeSpan.FromMilliseconds(120));
            }

            var grid = (UniformGrid)window.FindName("TileGrid")!;
            var addButton = (Button)window.FindName("AddButton")!;
            var cancelButton = (Button)window.FindName("SelectionCancelButton")!;
            var deleteButton = (Button)window.FindName("SelectionDeleteButton")!;
            var search = (TextBox)window.FindName("SearchBox")!;
            Check("批量删除：初始为普通模式（只有「添加」）", "L-26",
                grid.Children.Count == 35 && !Selecting(deleteButton)
                && addButton.Visibility == Visibility.Visible && cancelButton.Visibility == Visibility.Collapsed,
                "35 项、添加可见、取消/删除隐藏",
                $"{grid.Children.Count} 项、添加 {addButton.Visibility}、取消 {cancelButton.Visibility}、删除 {deleteButton.Visibility}");
            BeginSelection(window, "wpf-00");
            Pump(TimeSpan.FromMilliseconds(60));
            var selectingCount = SelectedIds(window);
            Check("批量删除：多选时「添加」旁边出现「取消选择」与「删除」", "L-26",
                Selecting(deleteButton) && selectingCount.Count == 1 && selectingCount.Contains("wpf-00")
                && addButton.Visibility == Visibility.Visible && cancelButton.Visibility == Visibility.Visible
                && deleteButton.IsEnabled && cancelButton.Content?.ToString() == "取消选择",
                "添加保留、取消选择/删除可见、已选 1 项、删除可用",
                $"添加 {addButton.Visibility}、取消选择 {cancelButton.Visibility}（内容 {cancelButton.Content}）"
                + $"、删除 {deleteButton.Visibility}（可用 {deleteButton.IsEnabled}，内容 {deleteButton.Content}）"
                + $"、已选 {selectingCount.Count}");
            Check("批量删除：多选时「取消选择」紧挨在「添加」左侧", "L-26",
                HeaderOrder(window) == "搜索应用|取消选择|添加|删除",
                "搜索框 | 取消选择 | 添加 | 删除",
                HeaderOrder(window));
            Check("批量删除：多选期间勾选角标可见", "L-26", BadgeIsVisible(grid.Children[0]), "角标可见",
                BadgeDiagnostics(0, grid.Children[0]));

            // ---- 右键菜单：真实 BuildTileMenu 产物必须带「多选…」，且点它真的进入多选 ----
            EndSelection(window);
            var menu = BuildTileMenu(window, "wpf-00");
            var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();
            Check("批量删除：右键菜单含「多选…」入口", "L-26",
                headers.Contains("多选…") && headers.Contains("打开") && headers.Contains("删除"),
                "菜单含 打开 / 删除 / 多选…",
                "菜单项：" + string.Join(" | ", headers));
            var multiSelectItem = menu.Items.OfType<MenuItem>().First(item => item.Header?.ToString() == "多选…");
            multiSelectItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(80));
            var afterMenuItem = SelectedIds(window);
            Check("批量删除：点菜单「多选…」进入多选并勾选该项", "L-26",
                Selecting(deleteButton) && afterMenuItem.Count == 1 && afterMenuItem.Contains("wpf-00"),
                "删除按钮可见、已选 wpf-00",
                $"删除 {deleteButton.Visibility}、已选 [{string.Join(",", afterMenuItem)}]");
            menu = BuildTileMenu(window, "wpf-01");
            var escapeHeaders = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();
            Check("批量删除：多选中右键菜单改为「退出多选」", "L-26",
                escapeHeaders.Contains("退出多选") && !escapeHeaders.Contains("多选…"),
                "菜单含 退出多选、不含 多选…",
                "菜单项：" + string.Join(" | ", escapeHeaders));
            EndSelection(window);
            Pump(TimeSpan.FromMilliseconds(60));

            // ---- 退出多选的两种方式 ----
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(TimeSpan.FromMilliseconds(60));
            Check("批量删除：Esc 退出多选并清空勾选", "L-26",
                !Selecting(deleteButton) && SelectedIds(window).Count == 0 && !BadgeIsVisible(grid.Children[0])
                && addButton.Visibility == Visibility.Visible,
                "删除隐藏、添加恢复、无勾选、角标隐藏",
                $"删除 {deleteButton.Visibility}、添加 {addButton.Visibility}、已选 {SelectedIds(window).Count}、{BadgeDiagnostics(0, grid.Children[0])}");

            BeginSelection(window, "wpf-01");
            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(60));
            Check("批量删除：取消按钮退出多选", "L-26", !Selecting(deleteButton) && SelectedIds(window).Count == 0,
                "删除隐藏、无勾选", $"删除 {deleteButton.Visibility}、已选 {SelectedIds(window).Count}");

            // ---- Del 键不参与删除（只有顶部「删除」按钮能删） ----
            BeginSelection(window, "wpf-00");
            var beforeDeleteKeyPress = LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(TimeSpan.FromMilliseconds(80));
            Check("批量删除：Del 键不触发删除，多选仍保持", "L-27",
                Selecting(deleteButton) && SelectedIds(window).Count == 1
                && LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count == beforeDeleteKeyPress,
                "布局不变、多选保持",
                $"布局 {beforeDeleteKeyPress} → {LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count}、"
                + $"删除 {deleteButton.Visibility}、已选 {SelectedIds(window).Count}");
            EndSelection(window);

            // ---- 主路径：顶部「删除」按钮，勾选 3 项 ----
            var libraryBefore = data.Library.Items.Count;
            var layoutBefore = LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count;
            BeginSelection(window, "wpf-00");
            ToggleByTag(window, grid, "wpf-10");
            ToggleByTag(window, grid, "wpf-20");
            Check("批量删除：点选累计为 3 项且按钮显示数量", "L-26",
                SelectedIds(window).Count == 3 && deleteButton.Content?.ToString()?.Contains('3') == true,
                "已选 3 项、按钮显示 3", $"已选 {SelectedIds(window).Count}、按钮 {deleteButton.Content}");
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(120));
            var afterIds = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("批量删除：顶部删除按钮移除全部勾选项", "L-26",
                !afterIds.Contains("wpf-00") && !afterIds.Contains("wpf-10") && !afterIds.Contains("wpf-20")
                && afterIds.Count == layoutBefore - 3,
                "3 项消失、布局少 3 项",
                $"布局 {layoutBefore} → {afterIds.Count}；残留 {afterIds.Count(id => id is "wpf-00" or "wpf-10" or "wpf-20")}");
            Check("批量删除：只改布局，应用库记录保留", "L-26",
                data.Library.Items.Count == libraryBefore && data.Library.Items.Any(item => item.Id == "wpf-00"),
                $"{libraryBefore} 条库记录不变且 wpf-00 仍在",
                $"库 {data.Library.Items.Count} 条；wpf-00 在库中 {data.Library.Items.Any(item => item.Id == "wpf-00")}");
            Check("批量删除：删除后退出多选、角标收起", "L-26",
                !Selecting(deleteButton) && SelectedIds(window).Count == 0 && addButton.Visibility == Visibility.Visible
                && !grid.Children.OfType<UIElement>().Any(BadgeIsVisible),
                "添加恢复、无勾选、整页无可见角标",
                $"删除 {deleteButton.Visibility}、添加 {addButton.Visibility}、已选 {SelectedIds(window).Count}、{BadgeDiagnostics(0, grid.Children[0])}");
            var reloaded = new GlassDeskDataStore(new AppPaths(root));
            reloaded.LoadAll();
            Check("批量删除：删除结果已落盘", "L-26",
                !LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).Contains("wpf-00"),
                "重载后布局不含被删项",
                $"重载后 {LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).Count} 项");

            // ---- Del 键不得删除：多选仍在，布局不动，须用顶部按钮 ----
            BeginSelection(window, "wpf-02");
            var beforeDel = LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(TimeSpan.FromMilliseconds(120));
            var afterDeleteKey = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("批量删除：Del 键不删除，勾选与多选状态保持", "L-27",
                Selecting(deleteButton) && SelectedIds(window).Count == 1 && afterDeleteKey.Count == beforeDel,
                "布局不变、仍处于多选",
                $"布局 {beforeDel} → {afterDeleteKey.Count}、删除 {deleteButton.Visibility}、已选 {SelectedIds(window).Count}");
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(120));
            var afterTopButton = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("批量删除：改用顶部按钮后才真正删除", "L-27",
                !afterTopButton.Contains("wpf-02") && !Selecting(deleteButton),
                "wpf-02 消失、退出多选",
                $"wpf-02 残留 {afterTopButton.Contains("wpf-02")}、删除 {deleteButton.Visibility}");

            // ---- 跨页勾选：补足一页多一项，第 2 页的点选在第 1 页删除时不应丢失 ----
            var firstPageCapacity = 35;
            var needToOverflow = LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count;
            var filler = Enumerable.Range(0, Math.Max(0, firstPageCapacity + 1 - needToOverflow))
                .Select(offset => $"wpf-9{offset:00}").ToArray();
            foreach (var id in filler)
                data.Library.Add(new AppEntry { Id = id, DisplayName = $"App {id}", Target = Path.Combine(root, "missing-" + id), IsTargetMissing = true });
            data.Library.Save();
            data.Layouts.CommitLauncher(
                LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher, filler, firstPageCapacity),
                data.Layouts.LauncherVersion);
            window.RefreshLibrary();
            Pump(TimeSpan.FromMilliseconds(80));
            var allIds = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            var libraryUsable = data.Library.Items.Count(item => item.IsUsable);
            var layoutSlots = data.Layouts.Launcher.Pages.SelectMany(page => page.Slots).Count();
            var pageTwoId = allIds[^1];
            Check("批量删除：补项后重新分页为两页", "L-27",
                allIds.Count == firstPageCapacity + 1 && layoutSlots == firstPageCapacity + 1,
                $"{firstPageCapacity + 1} 项 → {firstPageCapacity} + 1",
                $"布局 {allIds.Count} 项（顶层槽位 {layoutSlots}）；库 {data.Library.Items.Count} 项（可用 {libraryUsable}）；"
                + $"页数 {Math.Max(1, (allIds.Count + firstPageCapacity - 1) / firstPageCapacity)}；"
                + $"网格显示 {grid.Children.Count} 项；页文本 {(window.FindName("PageText") as TextBlock)?.Text}");
            var previousPage = (Button)window.FindName("PreviousPage")!;
            var nextPage = (Button)window.FindName("NextPage")!;
            nextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(120));
            grid.UpdateLayout();
            var pageTwoCount = grid.Children.Count;
            BeginSelection(window, pageTwoId);
            previousPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(120));
            grid.UpdateLayout();
            var pageOneTile = grid.Children.OfType<Button>().First().Tag as string;
            ToggleByTag(window, grid, pageOneTile!);
            var crossPage = SelectedIds(window);
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(120));
            var afterCrossPage = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("批量删除：跨页勾选一次全部移除", "L-27",
                pageTwoCount == 1 && crossPage.Count == 2
                && !afterCrossPage.Contains(pageOneTile!) && !afterCrossPage.Contains(pageTwoId),
                "第 2 页 1 项、跨页 2 项均被移除",
                $"第 2 页 {pageTwoCount} 项、跨页已选 {crossPage.Count}、残留 {afterCrossPage.Count(id => id == pageOneTile || id == pageTwoId)}");

            // ---- 搜索中不得批量删除 ----
            search.Text = "App";
            Pump(TimeSpan.FromMilliseconds(80));
            BeginSelection(window, "wpf-06");
            Check("批量删除：搜索结果中拒绝进入多选", "L-26", !Selecting(deleteButton),
                "删除按钮保持隐藏", $"删除 {deleteButton.Visibility}、添加 {addButton.Visibility}");
            search.Clear();
            Pump(TimeSpan.FromMilliseconds(80));
        }
        catch (Exception ex)
        {
            _failed++;
            Cases.Add(new { name = "batch-delete-runner", ids = "L-26/L-27", driver = "WPF window", expected = "no exception",
                actual = ex.ToString(), status = "Failed" });
        }
        finally
        {
            window?.Close();
            ThemeService.Current?.Dispose();
            app.Shutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                artifact = typeof(LauncherWindow).Assembly.FullName,
                dataDirectory = root,
                passed = Cases.Count - _failed,
                failed = _failed,
                cases = Cases,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (supplied is null && Directory.Exists(root))
                try { Directory.Delete(root, true); } catch (IOException) { }
        }

        Console.WriteLine($"Batch delete: {Cases.Count - _failed} passed, {_failed} failed. Report: {report}");
        return _failed == 0 ? 0 : 1;
    }

    private static void BeginSelection(LauncherWindow window, string id)
        => Invoke(window, "BeginSelection", id);

    private static void EndSelection(LauncherWindow window) => Invoke(window, "EndSelection");

    /// <summary>调用产品自己的右键菜单构造，拿到真实菜单而不是复制一份菜单定义。</summary>
    private static ContextMenu BuildTileMenu(LauncherWindow window, string id)
    {
        var slots = (System.Collections.IEnumerable)typeof(LauncherWindow)
            .GetMethod("CurrentSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        var slot = slots.Cast<LauncherSlot>().First(candidate => candidate.ItemId == id);
        return (ContextMenu)typeof(LauncherWindow)
            .GetMethod("BuildTileMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [slot])!;
    }

    private static void Invoke(LauncherWindow window, string method, params object[] arguments)
        => typeof(LauncherWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, arguments);

    private static List<string> SelectedIds(LauncherWindow window)
        => [.. ((HashSet<string>)typeof(LauncherWindow)
            .GetField("_selectedIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window)!).OrderBy(id => id, StringComparer.Ordinal)];

    /// <summary>多选模式是否开启：由顶部「删除」按钮的可见性判断，界面即状态。</summary>
    private static bool Selecting(Button deleteButton) => deleteButton.Visibility == Visibility.Visible;

    /// <summary>顶部这一行从左到右、当前真正占位的控件，用来锁住「取消选择在添加左边」。</summary>
    /// <remarks>
    /// 填充元素（搜索框）在 DockPanel 里是最后被布到剩余空间的，视觉上却在最左，
    /// 所以这里先收填充元素，再按声明顺序收右靠元素。
    /// </remarks>
    private static string HeaderOrder(LauncherWindow window)
    {
        var header = (Panel)window.FindName("SearchHeader")!;
        header.UpdateLayout();
        var ordered = header.Children.OfType<FrameworkElement>().Where(child => child.ActualWidth > 0).ToList();
        var fill = ordered.LastOrDefault();
        if (fill is not null) ordered = [fill, .. ordered.Take(ordered.Count - 1)];
        return string.Join("|", ordered.Select(child => child switch
        {
            Button { Name: "AddButton" } => "添加",
            Button { Name: "SelectionCancelButton" } => "取消选择",
            Button { Name: "SelectionDeleteButton" } => "删除",
            _ => "搜索应用",
        }));
    }

    private static void ToggleByTag(LauncherWindow window, UniformGrid grid, string id)
    {
        var tile = grid.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, id))
            ?? throw new InvalidOperationException($"磁贴 {id} 不在当前页。");
        Invoke(window, "ToggleSelection", id);
    }

    /// <summary>按依赖属性读取勾选角标；磁贴刚建出来时模板还没应用，先跑一次布局。</summary>
    private static bool BadgeIsVisible(UIElement tile)
    {
        var badge = FindBadge(tile);
        return badge is not null && badge.Visibility == Visibility.Visible;
    }

    private static Border? FindBadge(UIElement tile)
    {
        if (tile is not FrameworkElement element) return null;
        element.ApplyTemplate();
        element.UpdateLayout();
        return FindVisual<Border>(element).FirstOrDefault(border => border.Name == "SelectionBadge");
    }

    /// <summary>角标自身状态 + 磁贴 Tag + IsSelected 属性 + 绑定状态，用于定位不一致。</summary>
    private static string BadgeDiagnostics(int index, UIElement tile)
    {
        var badge = FindBadge(tile);
        var isSelected = tile is FrameworkElement element
            ? element.GetType().GetProperty("IsSelected")?.GetValue(element)?.ToString() ?? "无属性"
            : "非元素";
        var binding = badge?.GetBindingExpression(UIElement.VisibilityProperty);
        var ownsBadge = badge is not null && ReferenceEquals(badge.TemplatedParent, tile);
        return $"磁贴[{index}] Tag={(tile as FrameworkElement)?.Tag ?? "null"} "
            + $"角标={(badge is null ? "未找到" : badge.Visibility.ToString())} "
            + $"角标TemplatedParent={(badge?.TemplatedParent?.GetType().Name ?? "null")} 归属本磁贴={ownsBadge} "
            + $"IsSelected={isSelected} 绑定={(binding is null ? "无" : binding.Status.ToString())}";
    }
}
