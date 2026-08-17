using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.DataIntegrityLab;

/// <summary>
/// data-integrity-lab: the flow built to stress instance-data writing.
/// <para>
/// lab-sequential → run-sequential (three sequential writes plus a duplicate-echo probe) →
/// lab-parallel → run-parallel (four parallel HTTP tasks) → lab-collect → auto → lab-completed
/// </para>
/// <para>
/// What matters here is not the state machine but the DATA: parallel branches write through
/// their own scopes, so a lost or duplicated write shows up as a missing key rather than as a
/// failed transition.
/// </para>
/// <para>
/// <b>Known red in the containerised environment.</b> <c>run-parallel</c> never settles — the
/// instance stays Busy indefinitely (confirmed at 120s, so this is a hang and not a timeout that
/// wants tuning), which keeps <see cref="FullLifecycle_ReachesLabCompleted"/> and
/// <see cref="ParallelTasks_AllLandTheirOwnKeys"/> red. <c>run-sequential</c> and the updateData
/// storm are unaffected. The settle budget below is deliberately modest: a known hang should cost
/// the suite a minute, not four.
/// </para>
/// </summary>
public class DataIntegrityLabTests : WorkflowTestBase
{
    private const string Workflow = "data-integrity-lab";

    public DataIntegrityLabTests(VNextTestEnvironment environment) : base(environment) { }

    private async Task<string> StartLabAsync() =>
        await StartAsync(Workflow, new { testId = $"lab-{Guid.NewGuid():N}"[..16] });

    [Fact]
    public async Task FullLifecycle_ReachesLabCompleted()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        await RunAcceptedAsync(Workflow, id, "run-parallel", settleTimeout: TimeSpan.FromSeconds(60));
        await WaitForInstanceStateAsync(Workflow, id, "lab-completed", timeout: TimeSpan.FromSeconds(90));

        var (state, status) = await GetInstanceStateAsync(Workflow, id);
        Assert.Equal("lab-completed", state);
        Assert.Equal("C", status);
    }

    [Fact]
    public async Task SequentialTasks_AllLandTheirOwnKeys()
    {
        // Three sequential writers in one transition: every one of them must survive the merge.
        // A lost write here is the classic symptom of a full-echo mapping overwriting a peer.
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        var attributes = await GetAttributesAsync(Workflow, id);
        var keys = attributes.EnumerateObject().Select(p => p.Name).ToList();

        Assert.True(keys.Count > 1,
            $"the sequential step wrote nothing beyond the start payload — {string.Join(",", keys)}");
    }

    [Fact]
    public async Task ParallelTasks_AllLandTheirOwnKeys()
    {
        // Four parallel branches each write through their own DI scope; the merge must keep all
        // four. This is the case that originally exposed the shared-DbContext write collision.
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");
        await RunAcceptedAsync(Workflow, id, "run-sequential", settleTimeout: TimeSpan.FromSeconds(90));
        await WaitForInstanceStateAsync(Workflow, id, "lab-parallel", timeout: TimeSpan.FromSeconds(90));

        var before = (await GetAttributesAsync(Workflow, id)).EnumerateObject().Count();

        await RunAcceptedAsync(Workflow, id, "run-parallel", settleTimeout: TimeSpan.FromSeconds(60));
        await WaitForInstanceStateAsync(Workflow, id, "lab-completed", timeout: TimeSpan.FromSeconds(90));

        var after = (await GetAttributesAsync(Workflow, id)).EnumerateObject().Count();

        Assert.True(after > before,
            $"the parallel step added no keys (before={before}, after={after}) — " +
            "a parallel branch's write was lost in the merge");
    }

    [Fact]
    public async Task ConcurrentUpdateData_KeepsEveryAcceptedIncrement()
    {
        // updateData is admitted unconditionally, so several can be in flight at once. Each
        // ACCEPTED one must leave exactly one increment: a lost update means two writers merged
        // against the same stale head.
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        const int target = 5;
        var accepted = 0;
        var attempts = 0;

        while (accepted < target && attempts < target * 20)
        {
            attempts++;
            var status = await RunAsync(Workflow, id, "update-lab-progress",
                new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
            if ((int)status < 400) accepted++;
            await Task.Delay(150);
        }

        Assert.Equal(target, accepted);

        await WaitUntilSettledAsync(Workflow, id);
        await WaitUntilAsync(
            async () => await GetCounterAsync(Workflow, id, "labUpdateCount") >= target,
            $"labUpdateCount never reached {target} — an accepted updateData lost its increment: " +
            await DescribeAsync(Workflow, id),
            TimeSpan.FromSeconds(60));

        Assert.Equal(target, await GetCounterAsync(Workflow, id, "labUpdateCount"));
    }

    [Fact]
    public async Task Cancel_MovesTheLabToCancelled()
    {
        var id = await StartLabAsync();
        await WaitForInstanceStateAsync(Workflow, id, "lab-sequential");

        await RunAcceptedAsync(Workflow, id, "cancel-lab");
        await WaitForInstanceStateAsync(Workflow, id, "lab-cancelled", timeout: TimeSpan.FromSeconds(60));

        var (_, status) = await GetInstanceStateAsync(Workflow, id);
        Assert.True(TerminalStatuses.Contains(status), $"expected a terminal status, got {status}");
    }
}
