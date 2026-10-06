namespace HistoriaApp.Tests;

/// <summary>
/// Branch pill context menu: per-remote submenus with merge and remote delete, detached HEAD
/// merge disabling, label truncation, and callback argument propagation.
/// </summary>
[Trait("tier", "ui")]
public sealed class BranchPillRemoteMenuTests
{
    private static IReadOnlyList<Control> SubmenuItems(MenuItem item) =>
        Assert.IsAssignableFrom<IEnumerable<Control>>(item.ItemsSource).ToList();

    private static void Click(Control item) =>
        ((MenuItem)item).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public void One_submenu_per_remote_with_merge_then_delete()
    {
        var submenus = BranchMenu.BuildRemoteBranchSubmenus(
            "feat",
            ["origin", "upstream"],
            "main",
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        Assert.Equal(2, submenus.Count);
        Assert.Equal("origin/feat", submenus[0].Header);
        Assert.Equal("upstream/feat", submenus[1].Header);

        foreach (var sub in submenus)
        {
            var children = SubmenuItems(sub);
            Assert.Equal(3, children.Count);
            var merge = Assert.IsType<MenuItem>(children[0]);
            Assert.Equal("Merge into 'main'…", merge.Header);
            Assert.True(merge.IsEnabled);
            Assert.IsType<Separator>(children[1]);
            var delete = Assert.IsType<MenuItem>(children[2]);
            Assert.Equal("Delete…", delete.Header);
            Assert.True(delete.IsEnabled);
        }
    }

    [AvaloniaFact]
    public void Merge_is_disabled_on_detached_head()
    {
        var submenus = BranchMenu.BuildRemoteBranchSubmenus(
            "feat",
            ["origin"],
            currentBranch: null,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        Assert.Single(submenus);
        var children = SubmenuItems(submenus[0]);
        Assert.Equal(3, children.Count);
        var merge = Assert.IsType<MenuItem>(children[0]);
        Assert.Equal("Merge into current branch", merge.Header);
        Assert.False(merge.IsEnabled);
    }

    [AvaloniaFact]
    public void Long_names_are_trimmed_but_callbacks_get_full_ref()
    {
        var longBranch = new string('x', 40);
        string? mergedRef = null;
        string? deletedRemote = null;

        var submenus = BranchMenu.BuildRemoteBranchSubmenus(
            longBranch,
            ["origin"],
            "main",
            r => { mergedRef = r; return Task.CompletedTask; },
            rem => { deletedRemote = rem; return Task.CompletedTask; });

        Assert.Single(submenus);
        var header = submenus[0].Header?.ToString();
        Assert.NotNull(header);
        Assert.EndsWith("…", header);
        Assert.True(header.Length <= BranchMenu.MenuNameMaxChars);

        var children = SubmenuItems(submenus[0]);
        Click(children[0]);
        Click(children[2]);

        Assert.Equal($"origin/{longBranch}", mergedRef);
        Assert.Equal("origin", deletedRemote);
    }

    [AvaloniaFact]
    public void No_remotes_builds_no_submenus()
    {
        var submenus = BranchMenu.BuildRemoteBranchSubmenus(
            "feat",
            [],
            "main",
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        Assert.Empty(submenus);
    }

    [Fact]
    public void TruncateBranchLabelForMenu_preserves_short_names_and_ellipsizes_long_ones()
    {
        Assert.Equal("main", BranchMenu.TruncateBranchLabelForMenu("main"));
        var exact = new string('a', BranchMenu.MenuNameMaxChars);
        Assert.Equal(exact, BranchMenu.TruncateBranchLabelForMenu(exact));
        var oneOver = new string('b', BranchMenu.MenuNameMaxChars + 1);
        var truncated = BranchMenu.TruncateBranchLabelForMenu(oneOver);
        Assert.Equal(BranchMenu.MenuNameMaxChars, truncated.Length);
        Assert.EndsWith("…", truncated);
        Assert.Equal(oneOver[..(BranchMenu.MenuNameMaxChars - 1)] + "…", truncated);
    }
}
