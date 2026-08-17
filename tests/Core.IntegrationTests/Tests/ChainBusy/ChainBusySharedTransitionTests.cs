using System.Globalization;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ChainBusy;

/// <summary>
/// Shared transition routing and <c>$self</c> semantics.
/// <para>
/// A shared transition requested on a parent that holds an active SubFlow is handled by the
/// parent when the parent itself declares it, and relayed down the chain when it does not.
/// </para>
/// <para>
/// <b>A <c>$self</c> target does NOT skip the state's lifecycle here.</b> <c>target: $self</c> says
/// "do not move the instance" — it does not say "skip the state's hooks". So the state's OnExit and
/// OnEntry both fire and its scheduled transitions are cancelled and re-armed, exactly as they did
/// before the self-target profile existed. The lifecycle skip belongs to <c>updateData</c> alone;
/// <see cref="ChainBusyUpdateDataTests"/> asserts the opposite behaviour against the same states,
/// which is what pins the boundary between the two.
/// </para>
/// </summary>
public class ChainBusySharedTransitionTests : ChainBusyTestBase
{
    /// <summary>What <c>LeafExpireTimer.csx</c> arms: 30 minutes, never fires during a test run.</summary>
    private static readonly TimeSpan LeafTimerDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Slack between firing the transition and the runtime evaluating the timer. Must stay well
    /// below the delay used to separate the two candidate instants, or the assertion stops
    /// discriminating.
    /// </summary>
    private static readonly TimeSpan ReArmTolerance = TimeSpan.FromSeconds(10);

    public ChainBusySharedTransitionTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task SelfSharedTransition_RunsItsOwnWork_AndTheStatesLifecycle()
    {
        var chain = await BuildChainAsync("shared-self");

        var entriesBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafEntries");
        var exitsBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafExits");
        var marksBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks");

        // The leaf must already have entered leaf-waiting, otherwise the entry counter would be
        // moving for the wrong reason.
        Assert.True(entriesBefore >= 1, "precondition: the leaf never entered leaf-waiting");

        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "leaf-only-mark");
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks") > marksBefore,
            "the shared transition's onExecute never ran on the leaf");

        // The state is left and re-entered, so both hooks run exactly once more.
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafEntries") > entriesBefore,
            $"a $self shared transition did not re-run the state's OnEntry — {await DescribeAsync(LeafWorkflow, chain.LeafId)}");

        Assert.Equal(entriesBefore + 1, await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafEntries"));
        Assert.Equal(exitsBefore + 1, await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafExits"));
    }

    /// <summary>
    /// CancelScheduledJobs (39) tears the state's timer down and Schedule (80) arms a fresh one, so
    /// the execution instant moves to "now + the timer's duration". A real domain should know this:
    /// a frequently invoked <c>$self</c> shared transition on a short-timeout state defers that
    /// timeout on every call.
    /// <para>
    /// <b>Measured with a single, cache-cold read on purpose.</b> The obvious shape — read the armed
    /// instant, fire, read again, assert it moved — cannot work here: the state function is a
    /// fingerprint-validated cache and the job set is deliberately NOT part of the fingerprint, so a
    /// same-state re-arm serves the STALE entry (documented accepted gap,
    /// <c>docs/runtime/state-function-cache-and-etag.md</c>). The first read would populate the
    /// cache and the second would return it unchanged — the test would report "not re-armed" for
    /// every run, whatever the runtime did. So the leaf's state function is read exactly once, after
    /// the transition, and the instant is compared against the wall clock instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SelfSharedTransition_ReArmsTheScheduledTransition()
    {
        var chain = await BuildChainAsync("shared-sched");

        // Separate the two candidate instants: a re-armed timer lands at firedAt + duration, an
        // untouched one at chainBuiltAt + duration. Without a gap they are indistinguishable.
        await Task.Delay(TimeSpan.FromSeconds(20));

        var marksBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks");
        var firedAt = DateTimeOffset.UtcNow;

        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "leaf-only-mark");
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks") > marksBefore,
            "the shared transition never ran");
        await WaitUntilSettledAsync(LeafWorkflow, chain.LeafId);

        var armed = await GetScheduledExecuteAtAsync(LeafWorkflow, chain.LeafId, ScheduledTransitionName);
        Assert.NotNull(armed);

        var armedAt = DateTimeOffset.Parse(armed!, null, DateTimeStyles.AdjustToUniversal);
        var earliestIfReArmed = firedAt + LeafTimerDuration - ReArmTolerance;

        Assert.True(armedAt >= earliestIfReArmed,
            $"the timer was not re-armed: armed at {armedAt:O}, but a re-arm at {firedAt:O} would " +
            $"land no earlier than {earliestIfReArmed:O} — this is the instant from the ORIGINAL " +
            "arming, so CancelScheduledJobs (39) / Schedule (80) did not run.");
    }

    [Fact]
    public async Task ParentOwnedSharedTransition_IsHandledByTheParent_AndNotForwarded()
    {
        var chain = await BuildChainAsync("shared-parent");

        var parentMarksBefore = await GetCounterAsync(RootWorkflow, chain.RootId, "rootSharedMarks");
        var leafMarksBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks");
        var parentEntriesBefore = await GetCounterAsync(RootWorkflow, chain.RootId, "rootEntries");
        var parentExitsBefore = await GetCounterAsync(RootWorkflow, chain.RootId, "rootExits");

        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "root-shared-mark");
        await WaitUntilAsync(
            async () => await GetCounterAsync(RootWorkflow, chain.RootId, "rootSharedMarks") > parentMarksBefore,
            "the parent's own shared transition never ran on the parent");

        var leafMarksAfter = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks");
        var parentState = await GetInstanceStateAsync(RootWorkflow, chain.RootId);

        // Routing: handled locally, never relayed to the leaf, and the parent does not move.
        Assert.Equal(leafMarksBefore, leafMarksAfter);
        Assert.Equal("root-waiting", parentState.State);

        // ...but the parent's own state lifecycle does run, $self target notwithstanding.
        await WaitUntilAsync(
            async () => await GetCounterAsync(RootWorkflow, chain.RootId, "rootEntries") > parentEntriesBefore,
            $"the parent's $self shared transition did not re-run its OnEntry — {await DescribeAsync(RootWorkflow, chain.RootId)}");

        Assert.Equal(parentExitsBefore + 1, await GetCounterAsync(RootWorkflow, chain.RootId, "rootExits"));
    }

    [Fact]
    public async Task SharedTransitionDeclaredOnlyOnTheLeaf_IsForwardedDownTheChain()
    {
        var chain = await BuildChainAsync("shared-fwd");

        var leafBefore = await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks");
        var rootBefore = await GetCounterAsync(RootWorkflow, chain.RootId, "rootSharedMarks");
        var middleBefore = await GetCounterAsync(MiddleWorkflow, chain.MiddleId, "middleSharedMarks");

        // The root does not declare this key, so it must not try to handle it locally.
        await RunTransitionAsyncModeAsync(RootWorkflow, chain.RootId, "leaf-only-mark");
        await WaitUntilAsync(
            async () => await GetCounterAsync(LeafWorkflow, chain.LeafId, "leafOnlyMarks") > leafBefore,
            "the shared transition was not forwarded to the leaf");

        Assert.Equal(rootBefore, await GetCounterAsync(RootWorkflow, chain.RootId, "rootSharedMarks"));
        Assert.Equal(middleBefore, await GetCounterAsync(MiddleWorkflow, chain.MiddleId, "middleSharedMarks"));
    }
}
