using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ChainBusy;

/// <summary>
/// Cancel propagation in both directions.
/// <para>
/// The two directions travel differently, and that difference decides what this project can
/// cover. Cancelling the leaf settles its correlation and resumes the parent IN PROCESS, so it
/// works anywhere. Cancelling the root emits a <c>ChildSubflowCancelRequestedEvent</c> per open
/// correlation — a DISTRIBUTED event that only reaches the descendants once the Outbox worker
/// publishes it and the Inbox worker consumes it.
/// </para>
/// </summary>
public class ChainBusyCancelTests : ChainBusyTestBase
{
    public ChainBusyCancelTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task CancelOnTheLeaf_SettlesTheLeaf_AndPropagatesCompletionUpward()
    {
        var chain = await BuildChainAsync("cancel-up");

        await RunTransitionAsyncModeAsync(LeafWorkflow, chain.LeafId, "cancel-chain-busy-leaf");

        await WaitUntilTerminalAsync(LeafWorkflow, chain.LeafId, TimeSpan.FromSeconds(60));
        await WaitUntilTerminalAsync(MiddleWorkflow, chain.MiddleId, TimeSpan.FromSeconds(60));
        await WaitUntilTerminalAsync(RootWorkflow, chain.RootId, TimeSpan.FromSeconds(60));

        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);
        var middle = await GetInstanceStateAsync(MiddleWorkflow, chain.MiddleId);
        var root = await GetInstanceStateAsync(RootWorkflow, chain.RootId);

        Assert.Equal("leaf-cancelled", leaf.State);

        // The ancestors are not cancelled — they are told the subflow finished and unwind
        // through their own completion path.
        Assert.Equal("middle-done", middle.State);
        Assert.Equal("root-done", root.State);
    }

    [Fact]
    public async Task CancelOnTheLeaf_ClosesTheParentsCorrelation()
    {
        var chain = await BuildChainAsync("cancel-corr");

        await RunTransitionAsyncModeAsync(LeafWorkflow, chain.LeafId, "cancel-chain-busy-leaf");
        await WaitUntilTerminalAsync(RootWorkflow, chain.RootId, TimeSpan.FromSeconds(60));

        var response = await Api.CallInstanceFunctionAsync(RootWorkflow, chain.RootId, "state");
        var openCorrelations = response.Body.GetProperty("activeCorrelations").GetArrayLength();

        Assert.Equal(0, openCorrelations);
    }
    
    public async Task CancelOnTheRoot_CascadesDownTheWholeChain()
    {
        var chain = await BuildChainAsync("cancel-down");

        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "cancel-chain-busy-root");

        await WaitUntilTerminalAsync(RootWorkflow, chain.RootId, TimeSpan.FromSeconds(120));
        await WaitUntilTerminalAsync(MiddleWorkflow, chain.MiddleId, TimeSpan.FromSeconds(120));
        await WaitUntilTerminalAsync(LeafWorkflow, chain.LeafId, TimeSpan.FromSeconds(120));

        var root = await GetInstanceStateAsync(RootWorkflow, chain.RootId);
        var middle = await GetInstanceStateAsync(MiddleWorkflow, chain.MiddleId);
        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);

        Assert.Equal("root-cancelled", root.State);
        Assert.Equal("middle-cancelled", middle.State);
        Assert.Equal("leaf-cancelled", leaf.State);
    }
}
