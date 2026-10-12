namespace Historia.ViewModels;

/// <summary>
/// One line of the changed-files list. <see cref="Indent"/> is the left offset in pixels of the
/// row's first glyph, so the view can indent without arithmetic of its own.
/// </summary>
public abstract class CommitRowViewModel
{
    private protected CommitRowViewModel(double indent)
    {
        Indent = indent;
        ActivateCommand = new RelayCommand(Activate);
    }

    public double Indent { get; }

    /// <summary>What a click on the row does: a folder collapses or expands, a file opens its diff.</summary>
    public IRelayCommand ActivateCommand { get; }

    private protected abstract void Activate();
}

/// <summary>A folder line in tree mode.</summary>
public sealed class CommitFolderRowViewModel : CommitRowViewModel
{
    private readonly CommitDetailViewModel _owner;

    internal CommitFolderRowViewModel(CommitDetailViewModel owner, string folderPath, string name, int depth, bool collapsed)
        : base(CommitDetailViewModel.RowBaseIndent + depth * CommitDetailViewModel.IndentStep)
    {
        _owner = owner;
        FolderPath = folderPath;
        Name = name;
        IsCollapsed = collapsed;
    }

    /// <summary>Repo-relative path of the folder, forward slashes.</summary>
    public string FolderPath { get; }

    public string Name { get; }

    public bool IsCollapsed { get; }

    public string Label => Name + "/";

    public string Chevron => IsCollapsed ? "▸" : "▾";

    private protected override void Activate() => _owner.ToggleFolder(FolderPath);
}

/// <summary>A changed file, in path mode or beneath its folder in tree mode.</summary>
public sealed class CommitFileRowViewModel : CommitRowViewModel
{
    private readonly CommitDetailViewModel _owner;

    internal CommitFileRowViewModel(CommitDetailViewModel owner, FileStatusEntry file, string display, int depth)
        : base(CommitDetailViewModel.RowBaseIndent + depth * CommitDetailViewModel.IndentStep
               + (depth > 0 ? CommitDetailViewModel.IndentStep : 0))
    {
        _owner = owner;
        Path = file.Path;
        Display = display;
        Kind = file.Kind;
        RevealCommand = new RelayCommand(() => _owner.RevealFile(Path));
        CopyPathCommand = new AsyncRelayCommand(() => _owner.CopyFilePathAsync(Path));
    }

    /// <summary>Repo-relative path, forward slashes.</summary>
    public string Path { get; }

    /// <summary>The path in path mode, the file name in tree mode.</summary>
    public string Display { get; }

    public FileChangeKind Kind { get; }

    /// <summary>Shows the file in the file manager.</summary>
    public IRelayCommand RevealCommand { get; }

    /// <summary>Copies the file's absolute path to the clipboard.</summary>
    public IAsyncRelayCommand CopyPathCommand { get; }

    private protected override void Activate() => _owner.OpenFile(Path);
}

/// <summary>A parent commit in the header; clicking it selects and reveals that commit.</summary>
public sealed class CommitParentViewModel
{
    internal CommitParentViewModel(GitObjectId id)
    {
        Id = id;
        var sha = id.ToString();
        ShortHash = sha.Length >= 10 ? sha[..10] : sha;
    }

    public GitObjectId Id { get; }

    public string ShortHash { get; }
}
