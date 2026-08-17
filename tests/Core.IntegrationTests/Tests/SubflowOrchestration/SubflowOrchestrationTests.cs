using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowOrchestration;

/// <summary>
/// subflow-orchestration: the platform's three-level subflow reference
/// (parent → child → grandchild).
/// <para>
/// Unlike chain-busy, the chain here is gated: the parent rests in <c>parent-collect</c> until
/// enough <c>updateData</c> calls have landed, and each descendant needs a manual step. That
/// makes it the place to assert that updateData ADVANCES a flow — an accepted updateData writes
/// data and the state's auto transitions are then evaluated against that fresh data.
/// </para>
/// </summary>
public class SubflowOrchestrationTests : WorkflowTestBase
{
    private const string Parent = "subflow-orchestration-parent";
    private const string Child = "subflow-orchestration-child";
    private const string Grandchild = "subflow-orchestration-grandchild";

    private const int Threshold = 3;

    public SubflowOrchestrationTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Starts a parent and pushes updateData until the collect gate fires. Returns the parent id.
    /// </summary>
    private async Task<string> StartAndOpenTheGateAsync(string tag)
    {
        var parentId = await StartAsync(Parent, new { testId = $"{tag}-{Guid.NewGuid():N}"[..24], updateThreshold = Threshold });

        // parent-collect parks Busy at rest — it has an auto transition — so wait on the state,
        // never on the status.
        await WaitForInstanceStateAsync(Parent, parentId, "parent-collect");

        var accepted = 0;
        await WaitUntilAsync(async () =>
        {
            if (accepted < Threshold)
            {
                // updateData is admitted unconditionally, but a competing accept still yields a
                // 409 for the duplicate job — retry until exactly `Threshold` land.
                var status = await RunAsync(Parent, parentId, "update-parent-progress",
                    new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
                if ((int)status < 400) accepted++;
                await Task.Delay(200);
            }

            return (await GetInstanceStateAsync(Parent, parentId)).State == "parent-subflow-state";
        }, $"the collect gate never fired after {Threshold} accepted updates", TimeSpan.FromSeconds(90));

        return parentId;
    }

    [Fact]
    public async Task UpdateData_AdvancesTheFlow_WhenTheGateConditionIsMet()
    {
        var parentId = await StartAndOpenTheGateAsync("gate");

        var (state, _) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("parent-subflow-state", state);

        var count = await GetCounterAsync(Parent, parentId, "updateCount");
        Assert.True(count >= Threshold,
            $"the counter task lost increments: updateCount={count}, expected at least {Threshold}");
    }

    [Fact]
    public async Task OpeningTheGate_StartsTheChildSubflow()
    {
        var parentId = await StartAndOpenTheGateAsync("child");

        var subflows = await GetActiveSubflowsAsync(Parent, parentId);

        Assert.True(subflows.ContainsKey(Child), "the child subflow was not started");
        Assert.Equal("B", (await GetInstanceStateAsync(Parent, parentId)).Status);
    }

    [Fact]
    public async Task FullLifecycle_DrivesTheWholeChainAndUnwindsToParentCompleted()
    {
        var parentId = await StartAndOpenTheGateAsync("lifecycle");

        // The child rests in a manual state; the request is addressed to the PARENT and relayed
        // down the chain by the runtime.
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");
        await RunAsync(Parent, parentId, "proceed-to-subflow");

        // …which starts the grandchild, and the observed state follows it down another level.
        await WaitForObservedStateAsync(Parent, parentId, "grandchild-initial", timeout: TimeSpan.FromSeconds(60));
        await RunAsync(Parent, parentId, "complete-grandchild");

        // Completion then unwinds the whole chain back up to the parent.
        await WaitForInstanceStateAsync(Parent, parentId, "parent-completed", timeout: TimeSpan.FromSeconds(90));

        var (state, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("parent-completed", state);
        Assert.Equal("C", status);
    }

    [Fact]
    public async Task ParentSelfSharedTransition_RunsOnTheParent_WhileASubflowIsActive()
    {
        var parentId = await StartAndOpenTheGateAsync("shared");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");

        await RunAsync(Parent, parentId, "shared-common-transition");

        // $self on a SubFlow state: the parent handles it and does not leave the state.
        await WaitUntilAsync(
            async () => (await GetAttributesAsync(Parent, parentId)).TryGetProperty("sharedMarked", out _)
                        || (await GetInstanceStateAsync(Parent, parentId)).State == "parent-subflow-state",
            "the parent's shared transition never settled");

        Assert.Equal("parent-subflow-state", (await GetInstanceStateAsync(Parent, parentId)).State);
    }

    [Fact]
    public async Task UpdateDataOnAParentWithAnActiveSubflow_IsDataOnly_AndNeverRestartsTheSubflow()
    {
        // While a SubFlow correlation is open, updateData short-circuits to data-only well before
        // the transition's own work: the request payload is written, but onExecute never runs and
        // the subflow is neither restarted nor advanced. Restarting it here would be catastrophic,
        // which is exactly why the short-circuit exists.
        var parentId = await StartAndOpenTheGateAsync("data-only");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");

        var childBefore = (await GetActiveSubflowsAsync(Parent, parentId))[Child];
        var countBefore = await GetCounterAsync(Parent, parentId, "updateCount");
        var nonce = Guid.NewGuid().ToString("N")[..8];

        await RunAsync(Parent, parentId, "update-parent-progress", new { updateNonce = nonce });

        await WaitUntilAsync(async () =>
        {
            var attributes = await GetAttributesAsync(Parent, parentId);
            return attributes.TryGetProperty("updateNonce", out var value) && value.GetString() == nonce;
        }, "the updateData payload never landed on the parent");

        var subflowsAfter = await GetActiveSubflowsAsync(Parent, parentId);

        Assert.Equal("parent-subflow-state", (await GetInstanceStateAsync(Parent, parentId)).State);
        Assert.Equal(childBefore, subflowsAfter[Child]);

        // Data only: the counter task is an onExecute step, and the short-circuit skips it.
        Assert.Equal(countBefore, await GetCounterAsync(Parent, parentId, "updateCount"));
    }

    [Fact]
    public async Task Cancel_FromTheParent_TerminatesTheParent()
    {
        var parentId = await StartAndOpenTheGateAsync("cancel");
        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");

        await RunAsync(Parent, parentId, "cancel-parent");
        await WaitForInstanceStateAsync(Parent, parentId, "parent-cancelled", timeout: TimeSpan.FromSeconds(60));

        var (state, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("parent-cancelled", state);
        Assert.True(TerminalStatuses.Contains(status), $"expected a terminal status, got {status}");
    }
}
