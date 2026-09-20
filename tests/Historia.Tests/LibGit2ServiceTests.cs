namespace Historia.Tests;
[Trait("tier", "git")]

public class LibGit2ServiceTests
{
    private readonly LibGit2Service _svc = new();

    [Fact]
    public void Status_classifies_staged_unstaged_and_untracked()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-status-seed", r =>
        {
            r.WriteFile("tracked.txt", "one\n").Stage().Commit("initial");
            r.WriteFile("tracked.txt", "one\ntwo\n");   // unstaged modification
            r.WriteFile("added.txt", "new\n").Stage("added.txt"); // staged add
            r.WriteFile("untracked.txt", "loose\n");    // untracked
        });

        using var git = new Repository(repo.Path);
        var status = _svc.ProjectStatus(git);

        Assert.Contains(status.Staged, e => e.Path == "added.txt" && e.Kind == FileChangeKind.Added);
        Assert.Contains(status.Unstaged, e => e.Path == "tracked.txt" && e.Kind == FileChangeKind.Modified);
        Assert.Contains(status.Untracked, e => e.Path == "untracked.txt");
        Assert.True(status.IsDirty);
    }

    [Fact]
    public void Commits_are_paged_and_carry_head_decoration()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-paged-commits", r =>
        {
            r.WriteFile("f", "a").Stage().Commit("c1");
            r.WriteFile("f", "b").Stage().Commit("c2");
            r.WriteFile("f", "c").Stage().Commit("c3");
        });

        using var git = new Repository(repo.Path);
        var page = _svc.ProjectCommits(git, new CommitQuery { Skip = 0, Take = 2 });

        Assert.Equal(2, page.Count);
        Assert.Equal("c3", page[0].MessageShort);
        Assert.Contains(page[0].Decorations, d => d.Type == DecorationType.Head);
    }

    [Fact]
    public void Diff_for_a_commit_against_its_parent()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-diff-seed", r =>
        {
            r.WriteFile("f.txt", "line1\nline2\n").Stage().Commit("base");
            r.WriteFile("f.txt", "line1\nCHANGED\nline3\n").Stage().Commit("change");
        });
        var sha = repo.HeadSha;

        using var git = new Repository(repo.Path);
        var diff = _svc.ProjectDiff(git, new DiffRequest
        {
            Source = DiffSource.Commit,
            Path = "f.txt",
            CommitId = new ObjectId(sha),
        });

        Assert.False(diff.IsBinary);
        Assert.NotEmpty(diff.Hunks);
        Assert.True(diff.AddedLines > 0 && diff.DeletedLines > 0);
    }

    [Fact]
    public void Root_commit_diff_uses_the_empty_tree()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-root-seed", r =>
        {
            r.WriteFile("hello.txt", "hi\n").Stage().Commit("root");
        });
        var root = repo.HeadSha;

        using var git = new Repository(repo.Path);
        var diff = _svc.ProjectDiff(git, new DiffRequest
        {
            Source = DiffSource.Commit,
            Path = "hello.txt",
            CommitId = new ObjectId(root),
        });

        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.NotEmpty(diff.Hunks);
    }

    [Fact]
    public void Merge_commit_diff_can_target_each_parent()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-merge-diff-seed", r =>
        {
            r.WriteFile("base.txt", "base\n").Stage().Commit("base");
            r.Checkout("feature", create: true).WriteFile("feature.txt", "feat\n").Stage().Commit("on feature");
            r.Checkout("main").WriteFile("main.txt", "main\n").Stage().Commit("on main");
            r.Git("merge", "--no-ff", "feature", "-m", "merge feature");
        });
        var mergeSha = repo.HeadSha;

        using var git = new Repository(repo.Path);
        var vsParent1 = _svc.ProjectCommitFiles(git, new ObjectId(mergeSha), parentIndex: 1);
        var vsParent2 = _svc.ProjectCommitFiles(git, new ObjectId(mergeSha), parentIndex: 2);

        Assert.Contains(vsParent1, f => f.Path == "feature.txt");
        Assert.Contains(vsParent2, f => f.Path == "main.txt");
    }

    [Fact]
    public void Stage_then_commit_advances_head()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-stage-seed", r =>
        {
            r.WriteFile("a", "1").Stage().Commit("first");
            r.WriteFile("b", "2");
        });

        using var git = new Repository(repo.Path);
        _svc.Stage(git, ["b"]);
        var id = _svc.Commit(git, new CommitRequest
        {
            Message = "second",
            AuthorName = "Alice",
            AuthorEmail = "alice@example.com",
        });

        Assert.Equal(id, git.Head.Tip!.Id);
        Assert.Equal("Alice", git.Head.Tip.Author.Name);
    }

    [Fact]
    public void RepoHasCommitHooks_detects_an_installed_pre_commit_hook()
    {
        using var repo = RepoTemplates.Linear(1);
        var gitDir = Path.Combine(repo.Path, ".git");

        Assert.False(LibGit2Service.RepoHasCommitHooks(gitDir));
        File.WriteAllText(Path.Combine(gitDir, "hooks", "pre-commit"), "#!/bin/sh\nexit 0\n");
        Assert.True(LibGit2Service.RepoHasCommitHooks(gitDir));
    }

    [Fact]
    public void DiscardWorkingTree_handles_unstaged_untracked_and_staged()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("libgit2-discard-seed", r =>
        {
            r.WriteFile("tracked.txt", "original\n").Stage().Commit("init");
            r.WriteFile("tracked.txt", "modified\n");
            r.WriteFile("sub/nested.txt", "orig nested\n").Stage("sub/nested.txt").Commit("nested");
            r.WriteFile("sub/nested.txt", "mod nested\n");
            r.WriteFile("untracked.txt", "untracked\n");
            r.WriteFile("sub/untracked2.txt", "untracked2\n");
            r.WriteFile("staged_new.txt", "staged new\n").Stage("staged_new.txt");
            r.WriteFile("rename_me.txt", "rename me\n").Stage("rename_me.txt").Commit("rename init");
        });
        File.Move(Path.Combine(repo.Path, "rename_me.txt"), Path.Combine(repo.Path, "renamed.txt"));

        using var git = new Repository(repo.Path);
        _svc.DiscardWorkingTree(git, ["tracked.txt", "sub/nested.txt", "untracked.txt", "sub/untracked2.txt", "staged_new.txt", "renamed.txt", "rename_me.txt"]);

        var status = _svc.ProjectStatus(git);
        Assert.False(status.IsDirty);
        Assert.False(File.Exists(Path.Combine(repo.Path, "sub/untracked2.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "renamed.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "rename_me.txt")));
        Assert.Equal("orig nested", File.ReadAllText(Path.Combine(repo.Path, "sub/nested.txt")).Trim());
    }
}
