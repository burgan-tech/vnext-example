using System.Globalization;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ChainBusy;

/// <summary>
/// <c>updateData</c> writes data and runs its own work while the state lifecycle stays untouched:
/// no onExit, no onEntry, and no re-arming of the state's scheduled transitions.
/// <para>
/// <b><c>updateData</c> is the only transition that gets this.</b> Its target being <c>$self</c> is
/// not what earns it — <see cref="ChainBusySharedTransitionTests"/> fires a <c>$self</c> shared
/// transition against these same states and asserts the lifecycle DOES run. The two classes
/// together are what pin the boundary; changing one without the other silently erases it.
/// </para>
/// <para>
/// These run against the LEAF on purpose. A parent holding an open SubFlow correlation
/// short-circuits updateData to data-only much earlier in the pipeline, so the profile is only
/// fully exercised where there is no active subflow.
/// </para>
/// </summary>
public class ChainBusyUpdateDataTests : ChainBusyTestBase
{
    /// <summary>What <c>LeafExpireTimer.csx</c> arms: 30 minutes, never fires during a test run.</summary>
    private static readonly TimeSpan LeafTimerDuration = TimeSpan.FromMinutes(30);

    /// <summary>Slack between firing and the runtime evaluating the timer.</summary>
    private static readonly TimeSpan ReArmTolerance = TimeSpan.FromSeconds(10);

    public ChainBusyUpdateDataTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task UpdateData_RunsItsOwnWork_ButNotTheStatesLifecycle()
    {
        var chain = await BuildChainAsync("upd-lifecycle");

        var entriesBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafEntries");
        var exitsBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafExits");
        var updatesBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafUpdates");

        Assert.True(entriesBefore >= 1, "precondition: the leaf never entered leaf-waiting");

        await RunTransitionAsyncModeAsync(
            LeafWorkflow, chain.LeafId, "update-leaf-data", new { probe = Guid.NewGuid().ToString("N")[..6] });
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafUpdates") > updatesBefore,
            "updateData's onExecute never ran");

        Assert.Equal(entriesBefore, await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafEntries"));
        Assert.Equal(exitsBefore, await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafExits"));
    }

    /// <summary>
    /// The mirror image of <c>ChainBusySharedTransitionTests.SelfSharedTransition_ReArmsTheScheduledTransition</c>: updateData must leave the armed instant where the ORIGINAL arming put it.
    /// <para>
    /// <b>Measured with a single, cache-cold read, for the same reason as that test.</b> Reading the
    /// instant before and after and asserting they are equal proves nothing here: the state function
    /// is a fingerprint-validated cache, the job set is deliberately outside the fingerprint, and
    /// updateData does not change state or status — so the second read returns the first one's value
    /// whether the timer moved or not, and the assertion passes vacuously. Instead the leaf's state
    /// function is read exactly once, after the transition, and compared against the wall clock: a
    /// re-armed timer would land at firedAt + duration, an untouched one stays well before that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UpdateData_DoesNotReArmTheScheduledTransition()
    {
        var chain = await BuildChainAsync("upd-sched");

        // Separate the two candidate instants; without a gap they are indistinguishable.
        await Task.Delay(TimeSpan.FromSeconds(20));

        var updatesBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafUpdates");
        var firedAt = DateTimeOffset.UtcNow;

        await RunTransitionAsyncModeAsync(
            LeafWorkflow, chain.LeafId, "update-leaf-data", new { probe = Guid.NewGuid().ToString("N")[..6] });
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafUpdates") > updatesBefore,
            "updateData never ran");
        await WaitUntilSettledAsync(LeafWorkflow, chain.LeafId);

        var armed = await GetScheduledExecuteAtAsync(LeafWorkflow, chain.LeafId, ScheduledTransitionName);
        Assert.NotNull(armed);

        var armedAt = DateTimeOffset.Parse(armed!, null, DateTimeStyles.AdjustToUniversal);
        var earliestIfReArmed = firedAt + LeafTimerDuration - ReArmTolerance;

        Assert.True(armedAt < earliestIfReArmed,
            $"updateData re-armed the timer: armed at {armedAt:O}, which is at or past the " +
            $"{earliestIfReArmed:O} a re-arm at {firedAt:O} would produce — CancelScheduledJobs (39) " +
            "and Schedule (80) must stay excluded for updateData.");
    }

    [Fact]
    public async Task UpdateData_LeavesTheStateUnchanged()
    {
        var chain = await BuildChainAsync("upd-state");

        var updatesBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafUpdates");
        await RunTransitionAsyncModeAsync(
            LeafWorkflow, chain.LeafId, "update-leaf-data", new { probe = Guid.NewGuid().ToString("N")[..6] });
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafUpdates") > updatesBefore,
            "updateData never ran");
        await WaitUntilSettledAsync(LeafWorkflow, chain.LeafId);

        var leaf = await GetInstanceStateAsync(LeafWorkflow, chain.LeafId);

        Assert.Equal(LeafRestingState, leaf.State);
    }
}
