[assembly: TestCollectionOrderer("Historia.Tests.SlowestFirstCollectionOrderer", "Historia.Tests")]

namespace Historia.Tests;

/// <summary>
/// Starts the longest-running test classes first. xunit runs at most <c>maxParallelThreads</c>
/// collections at once and admits the rest in this order, and a class's own tests run one after
/// another — so a 50 s class admitted near the end of the run becomes the run's tail. Every other
/// collection keeps xunit's default order (by unique ID).
/// </summary>
public sealed class SlowestFirstCollectionOrderer : ITestCollectionOrderer
{
    // Slowest first, by summed test time in TRX runs of the full suite. Re-rank from a fresh
    // `dotnet test --logger trx` run when one class starts dominating the end of the run.
    private static readonly string[] Slowest =
    [
        "PatchServiceFuzzTests",
        "ConflictServiceTests",
        "GraphHeadlessCacheTests",
        "GraphFocusedHeadlessRaceTests",
        "GitGatewayWorkingTreeTests",
        "UnmergedIndexReaderTests",
        "FileDiffCliParityTests",
        "GitGatewayRefOpsTests",
        "PatchServiceTests",
        "GraphCliQueryShapeTests",
        "TestRepositoryBuilderTests",
        "GraphSpillTrunkStabilityTests",
        "PowerToolWriteLockTests",
        "CommitFilesCliParityTests",
        "WorkspaceTabGroupMoveTests",
        "StashServiceTests",
    ];

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderBy(Rank).ThenBy(c => c.UniqueID);

    private static int Rank(ITestCollection collection)
    {
        // A class's implicit collection is named "Test collection for <namespace>.<class>".
        var name = collection.DisplayName;
        var index = Array.IndexOf(Slowest, name[(name.LastIndexOf('.') + 1)..]);
        return index < 0 ? Slowest.Length : index;
    }
}
