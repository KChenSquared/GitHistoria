namespace Historia.Tests;

/// <summary>
/// Pre-packaged repository template shapes for fast fixture generation across tests (F1).
/// </summary>
public static class RepoTemplates
{
    public static TestRepositoryBuilder Linear(int commits) =>
        TestRepositoryBuilder.FromTemplate($"linear-{commits}", b => b.LinearHistory(commits));
}
