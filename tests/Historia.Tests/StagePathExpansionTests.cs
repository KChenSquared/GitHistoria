namespace Historia.Tests;

/// <summary>M3.2: <c>StagePathExpansion.ForStaging</c> — a selected rename expands to both
/// halves of the move so staging it produces one <c>Renamed</c> entry, not a stray add/delete.</summary>
[Trait("tier", "unit")]
public sealed class StagePathExpansionTests
{
    private static FileStatusEntry Entry(string path, FileChangeKind kind, string? oldPath = null) => new()
    {
        Path = path,
        Kind = kind,
        OldPath = oldPath,
        IsBinary = false,
        IsSubmodule = false,
    };

    [Fact]
    public void A_selected_rename_yields_both_paths()
    {
        var entries = new[] { Entry("new.txt", FileChangeKind.Renamed, "old.txt") };

        var expanded = StagePathExpansion.ForStaging(entries, ["new.txt"]);

        Assert.Equal(["new.txt", "old.txt"], expanded);
    }

    [Fact]
    public void A_selected_copy_yields_both_paths()
    {
        var entries = new[] { Entry("copy.txt", FileChangeKind.Copied, "source.txt") };

        var expanded = StagePathExpansion.ForStaging(entries, ["copy.txt"]);

        Assert.Equal(["copy.txt", "source.txt"], expanded);
    }

    [Fact]
    public void A_selected_modification_yields_one_path()
    {
        var entries = new[] { Entry("a.txt", FileChangeKind.Modified) };

        var expanded = StagePathExpansion.ForStaging(entries, ["a.txt"]);

        Assert.Equal(["a.txt"], expanded);
    }

    [Fact]
    public void A_rename_whose_old_path_is_null_yields_one_path()
    {
        var entries = new[] { Entry("new.txt", FileChangeKind.Renamed, oldPath: null) };

        var expanded = StagePathExpansion.ForStaging(entries, ["new.txt"]);

        Assert.Equal(["new.txt"], expanded);
    }

    [Fact]
    public void An_old_path_also_separately_selected_is_not_duplicated()
    {
        var entries = new[]
        {
            Entry("new.txt", FileChangeKind.Renamed, "old.txt"),
            Entry("old.txt", FileChangeKind.Deleted),
        };

        var expanded = StagePathExpansion.ForStaging(entries, ["new.txt", "old.txt"]);

        Assert.Equal(["new.txt", "old.txt"], expanded);
    }

    [Fact]
    public void Selection_order_is_preserved()
    {
        var entries = new[]
        {
            Entry("b.txt", FileChangeKind.Modified),
            Entry("new.txt", FileChangeKind.Renamed, "old.txt"),
            Entry("a.txt", FileChangeKind.Modified),
        };

        var expanded = StagePathExpansion.ForStaging(entries, ["b.txt", "new.txt", "a.txt"]);

        Assert.Equal(["b.txt", "new.txt", "old.txt", "a.txt"], expanded);
    }

    [Fact]
    public void An_empty_selection_yields_empty()
    {
        var entries = new[] { Entry("new.txt", FileChangeKind.Renamed, "old.txt") };

        var expanded = StagePathExpansion.ForStaging(entries, []);

        Assert.Empty(expanded);
    }
}
