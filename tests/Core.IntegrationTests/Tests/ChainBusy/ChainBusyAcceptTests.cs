using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ChainBusy;

/// <summary>
/// Async accept through the SubFlow transition proxy.
/// <para>
/// A client long polling on the root only ever observes the deepest active subflow, because
/// ancestors are Busy for their subflow's whole lifetime and that Busy carries no information.
/// So an async accept must make the leaf's in-flight work visible BEFORE it answers — otherwise
/// the caller gets its 202, polls, still sees the leaf Active, concludes nothing is in progress
/// and stalls the flow.
/// </para>
/// <para>
/// History (2026-10-08, vnext branch feature/file-offload-x-storage, vnext-client-sdk-core#101
/// Phase 2): this used to be done by an accept-time CHAIN RESERVE — the root locked and flipped
/// every level to Busy and relayed with a claim. The runtime now PROXIES instead: a parent with
/// an active S subflow forwards a forwardable transition to the leaf in the parent's resolved
/// mode, takes no lock and enqueues no job. The leaf does the admission (validation, its own
/// Busy compare-and-set, its own job). The first poll sees B because the leaf's CAS commits
/// before the 202 and the parent's EffectiveStatus is pre-stamped Busy.
/// </para>
/// </summary>
public class ChainBusyAcceptTests : ChainBusyTestBase
{
    public ChainBusyAcceptTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task AsyncAccept_ThroughTheProxy_ShowsBusyOnTheFirstPoll_BeforeAnsweringTheCaller()
    {
        var chain = await BuildChainAsync("accept");

        var before = await GetObservedStateAsync(RootWorkflow, chain.RootId);
        Assert.Equal("A", before.Status);

        var (status, _) = await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "finish-leaf");
        Assert.True(status is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"the accept was rejected with {status}");

        // Read immediately — no waiting. The leaf's own Busy CAS is committed before the 202
        // and the root's EffectiveStatus is pre-stamped Busy, so the very first poll sees B.
        var after = await GetObservedStateAsync(RootWorkflow, chain.RootId);

        Assert.Equal("B", after.Status);
    }

    [Fact]
    public async Task ForwardableRequestTheLeafRejects_ReturnsTheLeafsErrorSynchronously_SoNothingStaysBusy()
    {
        // E31 history (council 2026-09-08, P0). This test used to be
        // PostCommitForwardFailure_ReleasesTheChainReserve_SoNoLevelStaysBusy. Under the old
        // accept-time chain reserve the root accepted the request (202), marked every level Busy
        // and forwarded in a job; when the leaf then rejected it with a client error, the
        // post-commit failure policy declined to fault and nothing undid the reservation. Every
        // level stayed Busy permanently: Busy has no recovery API (retry requires Faulted) and no
        // incident is raised, so the instance was unreachable without direct database
        // intervention. Measured in production: 51 stranded instances in 30 days, across all
        // five live runtime versions. The first fix was a compensating release after the failed
        // forward.
        //
        // What changed (2026-10-08, vnext-client-sdk-core#101 Phase 2): the root no longer
        // reserves anything. A forwardable request is PROXIED to the leaf in the parent's resolved
        // mode and the leaf does the admission itself, so its rejection reaches the caller
        // SYNCHRONOUSLY — no 202, no later release. The guarantee therefore moved from "the
        // reserve is released" to "there was never a reserve": nothing was written on the root,
        // so there is nothing to undo and no way to strand a level.
        //
        // 'auto-leaf-to-waiting' is an automatic transition of the leaf that a user actor may not
        // fire, and the root has no such transition, so the root proxies it down. The leaf's
        // admission rejects it with Forbidden / Transition:100010; the response's 'target' is the
        // LEAF instance id (verified against the runtime: the error is the leaf's own, not one
        // the root produced).
        var chain = await BuildChainAsync("proxy-rejection");

        var (status, body) = await RunTransitionAsyncModeAsync(
            RootWorkflow, chain.RootId, "auto-leaf-to-waiting");

        Assert.True(status == HttpStatusCode.Forbidden,
            $"expected the leaf's rejection to come back synchronously as 403, got {status}: {body}");
        Assert.Equal("Transition:100010", body.GetProperty("code").GetString());
        Assert.Equal(chain.LeafId, body.GetProperty("target").GetString());

        // Nothing to release: the observation a client polling the root makes is a resting leaf.
        var observed = await GetObservedStateAsync(RootWorkflow, chain.RootId);
        Assert.Equal("A", observed.Status);
        Assert.Equal(LeafRestingState, observed.State);

        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);
        Assert.Equal("A", leaf.Status);
        Assert.Equal(LeafRestingState, leaf.State);

        // The ancestors keep their structural Busy: each holds an open SubFlow correlation and is
        // legitimately Busy for its child's whole lifetime. The rejection must not have touched
        // them — the proxy writes nothing on a parent.
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
        // The proxy hands the request to the leaf, which runs its own admission (Busy CAS, job).
        // If the hand-off ever stopped short of the leaf the chain would sit with every level Busy
        // and nothing running. (Under the old chain reserve this was the claim that let the relay
        // past the leaf's own Busy check, Instance:100031.)
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
