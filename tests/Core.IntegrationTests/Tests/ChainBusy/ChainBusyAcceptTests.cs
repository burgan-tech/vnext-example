using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ChainBusy;

/// <summary>
/// Accept-time SubFlow chain reserve.
/// <para>
/// A client long polling on the root only ever observes the deepest active subflow, because
/// ancestors are Busy for their subflow's whole lifetime and that Busy carries no information.
/// So an async accept must mark the chain down to the leaf BEFORE it answers — otherwise the
/// caller gets its 202, polls, still sees the leaf Active, concludes nothing is in progress and
/// stalls the flow.
/// </para>
/// </summary>
public class ChainBusyAcceptTests : ChainBusyTestBase
{
    public ChainBusyAcceptTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task AsyncAccept_MarksTheChainBusyDownToTheLeaf_BeforeAnsweringTheCaller()
    {
        var chain = await BuildChainAsync("accept");

        var before = await GetObservedStateAsync(RootWorkflow, chain.RootId);
        Assert.Equal("A", before.Status);

        var (status, _) = await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "finish-leaf");
        Assert.True(status is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"the accept was rejected with {status}");

        // Read immediately — no waiting. The reserve must already be committed.
        var after = await GetObservedStateAsync(RootWorkflow, chain.RootId);

        Assert.Equal("B", after.Status);
    }

    [Fact]
    public async Task AsyncAccept_RelaysTheTransitionAllTheWayToTheLeaf()
    {
        // The claim that lets the relay past the leaf's own Busy check is the other half of the
        // reserve: without it the forward is rejected with Instance:100031 and the chain
        // deadlocks with every level Busy and nothing running.
        var chain = await BuildChainAsync("relay");

        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "finish-leaf");

        await WaitUntilTerminalAsync(RootWorkflow, chain.RootId, TimeSpan.FromSeconds(90));

        var root = await GetInstanceStateAsync(RootWorkflow, chain.RootId);
        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);

        Assert.Equal("root-done", root.State);
        Assert.Equal("leaf-done", leaf.State);
        Assert.True(await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafFinishMarks") >= 1,
            "the leaf's finish-leaf onExecute never ran — the relay did not reach the leaf");
    }
}
