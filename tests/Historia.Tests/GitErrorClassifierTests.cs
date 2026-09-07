namespace Historia.Tests;

public class GitErrorClassifierTests
{
    [Theory]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", GitErrorKind.AuthenticationFailed)]
    [InlineData("fatal: Authentication failed for 'https://github.com/owner/repo.git/'", GitErrorKind.AuthenticationFailed)]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", GitErrorKind.HostKeyVerificationFailed)]
    [InlineData("fatal: unable to access 'https://x/': Could not resolve host: x", GitErrorKind.NetworkUnreachable)]
    [InlineData(" ! [rejected]        main -> main (non-fast-forward)", GitErrorKind.RemoteRejected)]
    // A rejected push also prints "failed to push some refs" and often a trailing
    // "fatal: unable to access … Could not resolve host" when git retries a second transport —
    // the strong rejection marker must still win over NetworkUnreachable.
    [InlineData(
        " ! [rejected]        main -> main (fetch first)\n"
        + "error: failed to push some refs to 'ssh-alias:owner/repo.git'\n"
        + "hint: Updates were rejected because the remote contains work that you do not\n"
        + "fatal: unable to access 'https://ssh-alias/owner/repo.git/': Could not resolve host: ssh-alias",
        GitErrorKind.RemoteRejected)]
    [InlineData("To github.com:owner/repo.git\n ! [remote rejected] main -> main (protected branch hook declined)", GitErrorKind.RemoteRejected)]
    [InlineData("CONFLICT (content): Merge conflict in file.txt\nAutomatic merge failed; fix conflicts and then commit the result.", GitErrorKind.MergeConflict)]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout:", GitErrorKind.UncommittedChanges)]
    [InlineData("fatal: Unable to create '/repo/.git/index.lock': File exists.", GitErrorKind.LockedIndex)]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", GitErrorKind.NotAGitRepository)]
    [InlineData("This repository is configured for Git LFS but 'git-lfs' was not found on your path.", GitErrorKind.LfsMissing)]
    [InlineData("fatal: refusing to merge unrelated histories", GitErrorKind.UnrelatedHistories)]
    public void Classify_maps_known_stderr(string stderr, GitErrorKind expected)
        => Assert.Equal(expected, GitErrorClassifier.Classify(exitCode: 128, stderr));

    [Fact]
    public void Unknown_stderr_is_never_silently_none()
        => Assert.Equal(GitErrorKind.Unknown, GitErrorClassifier.Classify(1, "some entirely novel failure text"));

    [Fact]
    public void Exit_zero_with_no_stderr_is_none()
        => Assert.Equal(GitErrorKind.None, GitErrorClassifier.Classify(0, ""));
}
