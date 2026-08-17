using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ChainBusy;

/// <summary>
/// Start semantics: an instance is pre-positioned into its initial state at creation and the
/// start transition then runs <c>initial → initial</c>. That shape looks like a self-transition
/// but is NOT one — the state is genuinely being entered, so its lifecycle must run.
/// </summary>
public class ChainBusyStartTests : ChainBusyTestBase
{
    public ChainBusyStartTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task Start_RunsTheInitialStatesOnEntry_ForATopLevelInstance()
    {
        var chain = await BuildChainAsync("start-root");

        var entries = await GetCounterAsync(RootWorkflow, chain.RootId, "rootInitialEntries");

        Assert.True(entries >= 1,
            "the top-level start did not run the initial state's onEntry — a start transition " +
            "whose target equals the current state must not be treated as a $self transition");
    }

    [Fact]
    public async Task Start_RunsTheInitialStatesOnEntry_ForASubflowInstance()
    {
        var chain = await BuildChainAsync("start-leaf");

        var entries = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafInitialEntries");

        Assert.True(entries >= 1,
            "the subflow start did not run the initial state's onEntry");
    }

    [Fact]
    public async Task BuildingTheChain_LeavesAncestorsBusyAndTheLeafActive()
    {
        // The premise every other chain-busy test rests on: ancestors are Busy for the whole
        // lifetime of their open SubFlow correlation, so only the leaf's status carries
        // information — and the state function reports exactly that leaf.
        var chain = await BuildChainAsync("chain-shape");

        var root = await GetInstanceStateAsync(RootWorkflow, chain.RootId);
        var middle = await GetInstanceStateAsync(MiddleWorkflow, chain.MiddleId);
        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);
        var observed = await GetObservedStateAsync(RootWorkflow, chain.RootId);

        Assert.Equal("B", root.Status);
        Assert.Equal("B", middle.Status);
        Assert.Equal("A", leaf.Status);
        Assert.Equal(LeafRestingState, leaf.State);

        Assert.Equal((LeafRestingState, "A"), observed);
    }
}
