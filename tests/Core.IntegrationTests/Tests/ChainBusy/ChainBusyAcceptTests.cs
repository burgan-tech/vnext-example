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
    public async Task PostCommitForwardFailure_ReleasesTheChainReserve_SoNoLevelStaysBusy()
    {
        // E31 (council 2026-09-08, P0). The other half of the reserve: what happens when the
        // forward it was taken for never succeeds.
        //
        // 'auto-leaf-to-waiting' exists in the leaf's definition but is not available in
        // 'leaf-waiting'. The root has no such transition, so it accepts the request, marks the
        // chain Busy down to the leaf and forwards — and the leaf rejects it with a Validation
        // error. The post-commit failure policy classifies that as the client's error, so it
        // declines to fault; that exit runs neither settlement nor fault, and before the fix
        // nothing undid the reservation. Every level stayed Busy permanently: Busy has no
        // recovery API (retry requires Faulted) and no incident is raised, so the instance was
        // unreachable without direct database intervention. Measured in production: 51 stranded
        // instances in 30 days, across all five live runtime versions.
        var chain = await BuildChainAsync("reserve-release");

        var (status, _) = await RunTransitionAsyncModeAsync(
            RootWorkflow, chain.RootId, "auto-leaf-to-waiting");
        Assert.True(status is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"the accept was rejected with {status} — the reserve was never taken, so this test " +
            "would pass without exercising the compensation");

        // The client polls the root and only ever sees the leaf. That is the observation the
        // compensation has to restore; before the fix it read "B" forever.
        await WaitUntilAsync(
            async () => (await GetObservedStateAsync(RootWorkflow, chain.RootId)).Status == "A",
            $"the chain stayed Busy after the forward failed — the accept-time reserve was not " +
            $"released. root={await DescribeAsync(RootWorkflow, chain.RootId)}",
            TimeSpan.FromSeconds(30));

        var observed = await GetObservedStateAsync(RootWorkflow, chain.RootId);
        Assert.Equal(LeafRestingState, observed.State);

        // The leaf owns its own visible status, so the release flips it back.
        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);
        Assert.Equal("A", leaf.Status);
        Assert.Equal(LeafRestingState, leaf.State);

        // The ancestors must NOT be released: each holds an open SubFlow correlation and is
        // legitimately Busy for its child's whole lifetime. Releasing them would settle a parent
        // that is still mid-subflow — the compensation undoes only what the reserve flipped.
        Assert.Equal("B", (await GetInstanceStateAsync(RootWorkflow, chain.RootId)).Status);
        Assert.Equal("B", (await GetInstanceStateAsync(MiddleWorkflow, chain.MiddleId)).Status);

        // Not merely un-Busied: the chain still works. This is what "stranded" cost in
        // production — the flow could never be driven to completion again.
        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "finish-leaf");
        await WaitUntilTerminalAsync(RootWorkflow, chain.RootId, TimeSpan.FromSeconds(90));

        Assert.Equal("root-done", (await GetInstanceStateAsync(RootWorkflow, chain.RootId)).State);
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
