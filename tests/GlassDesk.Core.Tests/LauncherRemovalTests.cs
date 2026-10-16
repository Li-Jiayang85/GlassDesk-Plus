using System.Text.Json;
using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherRemovalTests
{
    [Fact]
    public void TrashRemovalSurvivesReloadWithoutKeepingAHiddenList()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["keep", "remove"], 35);
        var removed = LauncherLayoutEditor.RemoveShortcut(initial, "remove", 35);
        var json = JsonSerializer.Serialize(removed);
        Assert.DoesNotContain("excludedAppIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(removed.ExcludedAppIds);
        var reloaded = JsonSerializer.Deserialize<LauncherLayout>(json)!;
        Assert.Equal(["keep"], LauncherLayoutRules.AppIds(
            LauncherLayoutRules.Reconcile(reloaded, ["keep", "remove", "dock-only"], 35)));
        Assert.Equal(["keep", "remove"], LauncherLayoutRules.AppIds(initial));
    }

    [Fact]
    public void RemovingAGroupDoesNotReturnItsMembersOnRefresh()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "keep"], 35);
        var grouped = LauncherLayoutEditor.CreateGroup(initial, "a", "b", "group", 35);
        var removed = LauncherLayoutEditor.RemoveShortcut(grouped, "group", 35);
        Assert.Empty(removed.ExcludedAppIds);
        Assert.Equal(["keep"], LauncherLayoutRules.AppIds(
            LauncherLayoutRules.Reconcile(removed, ["a", "b", "keep"], 35)));
    }

    [Fact]
    public void OldHiddenPreferencesMigrateToVisibleMembershipOnly()
    {
        var old = JsonSerializer.Deserialize<LauncherLayout>(
            """{"Pages":[{"Id":"p","Slots":[{"ItemId":"keep"}]}],"excludedAppIds":["old-hidden"]}""")!;
        var migrated = LauncherLayoutRules.Reconcile(old, ["keep", "old-hidden"], 35);
        Assert.Equal(["keep"], LauncherLayoutRules.AppIds(migrated));
        Assert.DoesNotContain("excludedAppIds", JsonSerializer.Serialize(migrated), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatchRemovalDropsEveryRequestedEntryAndKeepsTheRest()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "c", "d"], 35);
        var removed = LauncherLayoutEditor.RemoveShortcuts(initial, ["a", "c"], 35);
        Assert.Equal(["b", "d"], LauncherLayoutRules.AppIds(removed));
        // 批量删除与单个删除同义：不留隐藏名单，重新扫描也不会把它们带回来。
        Assert.Empty(removed.ExcludedAppIds);
        Assert.Equal(["b", "d"], LauncherLayoutRules.AppIds(
            LauncherLayoutRules.Reconcile(removed, ["a", "b", "c", "d"], 35)));
    }

    [Fact]
    public void BatchRemovalTakesGroupMembersOutAndCollapsesTheGroup()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "keep"], 35);
        var grouped = LauncherLayoutEditor.CreateGroup(initial, "a", "b", "group", 35);
        var removed = LauncherLayoutEditor.RemoveShortcuts(grouped, ["a"], 35);
        // 组里只剩一个成员时摊回普通图标，组壳不该留下。
        Assert.Equal(["b", "keep"], LauncherLayoutRules.AppIds(removed));
        Assert.DoesNotContain(removed.Pages.SelectMany(page => page.Slots), slot => slot.Kind == LauncherSlotKind.Group);
    }

    [Fact]
    public void BatchRemovalIgnoresUnknownAndRepeatedIds()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b"], 35);
        var removed = LauncherLayoutEditor.RemoveShortcuts(initial, ["a", "a", "never-existed", ""], 35);
        Assert.Equal(["b"], LauncherLayoutRules.AppIds(removed));
    }

    [Fact]
    public void BatchRemovingEverythingLeavesAnEmptyLayoutThatStillReconciles()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b"], 35);
        var removed = LauncherLayoutEditor.RemoveShortcuts(initial, ["a", "b"], 35);
        Assert.Empty(removed.Pages);
        Assert.Empty(LauncherLayoutRules.AppIds(LauncherLayoutRules.Reconcile(removed, ["a", "b"], 35)));
    }
}
