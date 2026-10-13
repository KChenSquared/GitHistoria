namespace Historia.ViewModels;

/// <summary>How a leaf row's label is coloured.</summary>
public enum RefRowTone
{
    Normal,
    Accent,
    Muted,
    Faint,
}

/// <summary>
/// One row of a ref navigator section. Rows are immutable: a change to the refs, a filter or an
/// expanded folder replaces the section's whole row list. A row keeps a reference to the view model
/// that built it so its menus and buttons bind to that view model's commands, passing the row as the
/// parameter.
/// </summary>
public abstract class RefRowViewModel
{
    protected RefRowViewModel(RefNavigatorViewModel host)
    {
        Host = host;
    }

    /// <summary>The navigator that owns this row.</summary>
    public RefNavigatorViewModel Host { get; }
}

/// <summary>The hairline between a filter's matches and the tree below them.</summary>
public sealed class RefSeparatorRowViewModel : RefRowViewModel
{
    internal RefSeparatorRowViewModel(RefNavigatorViewModel host)
        : base(host)
    {
    }
}

/// <summary>
/// A collapsible folder: a remote's own root, or one path segment of a branch name. A configured
/// remote's root carries the remote's context menu; every remote root shows a cloud, numbered when
/// two or more remotes are configured.
/// </summary>
public sealed class RefFolderRowViewModel : RefRowViewModel
{
    internal RefFolderRowViewModel(
        RefNavigatorViewModel host,
        RefSectionKind section,
        string segment,
        string path,
        int depth,
        bool isOpen,
        string tooltip,
        RemoteInfo? remote = null,
        bool isRemoteRoot = false,
        int cloudNumber = 0)
        : base(host)
    {
        Section = section;
        Segment = segment;
        Path = path;
        Depth = depth;
        IsOpen = isOpen;
        Tooltip = tooltip;
        Remote = remote;
        IsRemoteRoot = isRemoteRoot;
        CloudNumber = cloudNumber;
        var caretLeft = RefNavigatorViewModel.LeafIndent + depth * RefNavigatorViewModel.IndentStep - 12;
        CaretIndent = isRemoteRoot ? Math.Max(4, caretLeft - RefNavigatorViewModel.RemoteCloudSlot) : caretLeft;
    }

    /// <summary>The section the folder sits in; a toggle rebuilds only that section.</summary>
    public RefSectionKind Section { get; }

    /// <summary>The folder's own name (a path segment, or the remote's alias).</summary>
    public string Segment { get; }

    /// <summary>The key the expanded state is remembered under.</summary>
    public string Path { get; }

    public int Depth { get; }

    public bool IsOpen { get; }

    public string Caret => IsOpen ? "▾" : "▸";

    /// <summary>Width of the space before the caret, so the folder's name lands on the shared leaf indent.</summary>
    public double CaretIndent { get; }

    public string Tooltip { get; }

    /// <summary>The configured remote this row is the root of; null for a path segment or a stale group.</summary>
    public RemoteInfo? Remote { get; }

    public bool HasMenu => Remote is not null;

    /// <summary>True for a remote's root row, configured or stale; it shows the cloud.</summary>
    public bool IsRemoteRoot { get; }

    /// <summary>The remote's 1-based number, or 0 for a plain cloud.</summary>
    public int CloudNumber { get; }

    public bool HasCloudNumber => CloudNumber > 0;

    /// <summary>True for a two-digit number, which is drawn smaller so it fits inside the cloud.</summary>
    public bool IsWideCloudNumber => CloudNumber >= 10;

    /// <summary>The cloud's number as text, empty for a plain cloud.</summary>
    public string CloudNumberText => CloudNumber > 0 ? CloudNumber.ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>A plain leaf: a message such as "Loading…", the detached HEAD, or an action row.</summary>
public class RefLeafRowViewModel : RefRowViewModel
{
    internal RefLeafRowViewModel(
        RefNavigatorViewModel host,
        string text,
        int depth,
        RefRowTone tone,
        Action? press = null,
        bool isCurrent = false,
        string? tooltip = null)
        : base(host)
    {
        Text = text;
        Depth = depth;
        Tone = tone;
        Press = press;
        IsCurrent = isCurrent;
        Tooltip = tooltip ?? text;
    }

    public string Text { get; }

    public int Depth { get; }

    /// <summary>Width of the space before the marker slot; leaves and folder labels share one left edge.</summary>
    public double Indent => RefNavigatorViewModel.LeafIndent + Depth * RefNavigatorViewModel.IndentStep - 11;

    public RefRowTone Tone { get; }

    public bool IsAccent => Tone == RefRowTone.Accent;

    public bool IsMuted => Tone == RefRowTone.Muted;

    public bool IsFaint => Tone == RefRowTone.Faint;

    /// <summary>True for the checked-out branch or the detached HEAD: bold, with a marker dot.</summary>
    public bool IsCurrent { get; }

    public string Tooltip { get; }

    /// <summary>What a press on the row does; null for a row that only informs.</summary>
    internal Action? Press { get; }

    /// <summary>True when a press on the row does something.</summary>
    public bool CanPress => Press is not null;
}

/// <summary>A local or remote-tracking branch. Double-clicking checks it out.</summary>
public sealed class RefBranchRowViewModel : RefLeafRowViewModel
{
    internal RefBranchRowViewModel(
        RefNavigatorViewModel host,
        BranchInfo branch,
        bool isRemote,
        string text,
        int depth,
        bool isCurrent,
        Action press)
        : base(host, text, depth, isCurrent ? RefRowTone.Accent : RefRowTone.Normal, press, isCurrent, branch.Name)
    {
        Branch = branch;
        IsRemote = isRemote;
    }

    public BranchInfo Branch { get; }

    public bool IsRemote { get; }
}

/// <summary>A tag with a delete button and a context menu that pushes it to each remote.</summary>
public sealed class RefTagRowViewModel : RefLeafRowViewModel
{
    internal RefTagRowViewModel(RefNavigatorViewModel host, TagInfo tag, IReadOnlyList<string> remotes, Action press)
        : base(host, tag.Name, 0, RefRowTone.Normal, press)
    {
        Tag = tag;
        var name = RefMenuItemViewModel.TrimName(tag.Name);
        var items = remotes
            .Select(remote => new RefMenuItemViewModel($"Push {name} to {remote}", host.PushTagCommand, new RefTagPushTarget(tag, remote)))
            .ToList();
        if (items.Count > 0)
            items.Add(RefMenuItemViewModel.Separator);
        items.Add(new RefMenuItemViewModel($"Delete tag {name}", host.DeleteTagCommand, this));
        MenuItems = items;
    }

    public TagInfo Tag { get; }

    /// <summary>The context menu: one unforced push per remote, offered whether or not the remote
    /// already has the tag, then Delete.</summary>
    public IReadOnlyList<RefMenuItemViewModel> MenuItems { get; }
}

/// <summary>A tag and the remote a context menu item pushes it to.</summary>
public sealed record RefTagPushTarget(TagInfo Tag, string Remote);

/// <summary>A stash entry with an Apply, Apply and drop, New branch and Drop menu.</summary>
public sealed class RefStashRowViewModel : RefLeafRowViewModel
{
    internal RefStashRowViewModel(RefNavigatorViewModel host, StashEntry entry, Action press)
        : base(host, RefNavigatorViewModel.StashLabel(entry), 0, RefRowTone.Normal, press)
    {
        Entry = entry;
    }

    public StashEntry Entry { get; }
}

/// <summary>A submodule with an Init and update, Sync URL and Remove menu. Double-clicking opens its tab.</summary>
public sealed class RefSubmoduleRowViewModel : RefLeafRowViewModel
{
    internal RefSubmoduleRowViewModel(RefNavigatorViewModel host, SubmoduleInfo submodule)
        : base(host, RefNavigatorViewModel.SubmoduleRowLabel(submodule, busy: false), 0, RefRowTone.Normal,
            tooltip: submodule.Path)
    {
        Submodule = submodule;
    }

    public SubmoduleInfo Submodule { get; }
}

/// <summary>A worktree. A linked one has a Lock or Unlock and Remove menu; the main one has none.</summary>
public sealed class RefWorktreeRowViewModel : RefLeafRowViewModel
{
    internal RefWorktreeRowViewModel(RefNavigatorViewModel host, WorktreeInfo worktree)
        : base(host,
            $"{worktree.Branch}{(worktree.IsMainWorktree ? "  (main)" : "")}{(worktree.IsLocked ? "  🔒" : "")}",
            0, RefRowTone.Normal)
    {
        Worktree = worktree;
    }

    public WorktreeInfo Worktree { get; }

    public bool HasMenu => !Worktree.IsMainWorktree;

    public bool IsLocked => Worktree.IsLocked;

    public bool IsUnlocked => !Worktree.IsLocked;
}
