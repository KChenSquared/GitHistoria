namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class GitGatewayProfileFallbackTests
{
    [Theory]
    [InlineData(GitErrorKind.AuthenticationFailed)]
    [InlineData(GitErrorKind.NetworkUnreachable)]
    [InlineData(GitErrorKind.TimedOut)]
    [InlineData(GitErrorKind.Unknown)]
    public void CredentialOrReachabilityFailures_OfferLocalConfig(GitErrorKind kind) =>
        Assert.True(GitGateway.IsProfileFixable(kind));

    [Theory]
    [InlineData(GitErrorKind.RefNotFound)]
    [InlineData(GitErrorKind.RemoteRejected)]
    [InlineData(GitErrorKind.UncommittedChanges)]
    [InlineData(GitErrorKind.UnrelatedHistories)]
    [InlineData(GitErrorKind.MergeConflict)]
    [InlineData(GitErrorKind.HostKeyVerificationFailed)]
    public void FailuresAnotherProfileCannotFix_KeepTheirOwnHandling(GitErrorKind kind) =>
        Assert.False(GitGateway.IsProfileFixable(kind));
}
