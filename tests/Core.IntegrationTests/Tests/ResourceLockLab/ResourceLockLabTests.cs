using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ResourceLockLab;

/// <summary>
/// Transition-level resource locks (vnext#1143): who gets the lock, what happens to whoever loses
/// the race, and when the lock comes back.
/// <para>
/// The flow keeps one state, <c>rl-idle</c>, carrying every acquiring transition, and the lock key
/// is taken from the instance's own <c>lockKey</c> — two instances contend by being started with the
/// same value and stay independent with different ones. <c>rl-idle</c> declares an
/// <c>errorBoundary</c> rule for <c>Execution:200010</c> routing to the shared <c>rl-lock-busy</c>
/// transition, which is the behaviour under test in <see cref="WhileHeld_TheLoserIsReroutedByTheBoundary_AndNeitherInstanceFaults"/>:
/// before this change a lost race faulted the instance and the contended work was simply lost.
/// </para>
/// <para>
/// Deliberate coverage gaps, both tied to things outside this repo:
/// <list type="bullet">
/// <item><c>onConflict: "Reject"</c> is NOT exercised. The published schema
/// (<c>@burgan-tech/vnext-schema</c> 0.0.57) still constrains <c>onConflict</c> to <c>["Abort"]</c>;
/// <c>Reject</c> ships in vnext-schema#162. A flow using it fails this repo's own
/// <c>npm run validate</c>, so the case lands here once that schema is released. The runtime path is
/// covered by unit tests (<c>TransitionPipelineTests</c>, including the chained-hop case).</item>
/// <item><see cref="Extend_KeepsTheLeasePastItsOriginalTtl"/> only runs on the Postgres lease
/// provider. Dapr's lock building block has no extend — that absence is the reason the provider
/// setting exists — so on the default runtime the test skips rather than asserting a capability the
/// provider does not have.</item>
/// </list>
/// </para>
/// </summary>
public class ResourceLockLabTests : WorkflowTestBase
{
    private const string Workflow = "resource-lock-lab";

    /// <summary>
    /// TTL of <c>rl-acquire-short</c> in the flow definition. The Extend test has to outlive it for
    /// the assertion to mean anything.
    /// </summary>
    private static readonly TimeSpan ShortTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the runtime under test was started with <c>ResourceLock:Provider=postgres</c>. The
    /// runtime does not advertise its lock provider, so the runner states it — the same shape
    /// <c>MorphIdmProviderTests</c> uses for its provider-dependent cases.
    /// </summary>
    private static bool ProviderIsPostgres =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("VNEXT_RESOURCE_LOCK_PROVIDER"),
            "postgres",
            StringComparison.OrdinalIgnoreCase);

    public ResourceLockLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The holder reaches a terminal state and the next instance can take the same key.
    /// <para>
    /// This is the release-at-commit path: the lock is handed back on the unit of work's
    /// <c>OnCompleted</c> hook, not inline, so a transition that still rolls back never announces
    /// the section as free. If release were skipped or lost, the second instance would be rerouted
    /// to <c>rl-waiting</c> instead of holding, and this test would catch it as a state mismatch
    /// rather than a timeout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AfterTheHolderFinishes_TheSameKeyCanBeTakenAgain()
    {
        var key = NewKey();

        var first = await StartLabAsync(key);
        await RunAcceptedAsync(Workflow, first, "rl-acquire");
        await WaitForInstanceStateAsync(Workflow, first, "rl-holding");

        await RunAcceptedAsync(Workflow, first, "rl-finish");
        await WaitForInstanceStateAsync(Workflow, first, "rl-done");

        var second = await StartLabAsync(key);
        await RunAcceptedAsync(Workflow, second, "rl-acquire");

        await WaitForInstanceStateAsync(Workflow, second, "rl-holding");
        await AssertNotFaultedAsync(Workflow, second);
    }

    /// <summary>
    /// While the lock is held, the loser is rerouted by the state's error boundary instead of being
    /// faulted — and the holder is untouched by the contention.
    /// <para>
    /// The lock step runs before OnExecute/ChangeState/OnEntry, so the loser never left
    /// <c>rl-idle</c> and rerouting it is sound. Asserting the holder separately matters because the
    /// failure that would hurt most in production is not "the loser faulted" but "the loser's
    /// failure disturbed the winner".
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhileHeld_TheLoserIsReroutedByTheBoundary_AndNeitherInstanceFaults()
    {
        var key = NewKey();

        var holder = await StartLabAsync(key);
        await RunAcceptedAsync(Workflow, holder, "rl-acquire");
        await WaitForInstanceStateAsync(Workflow, holder, "rl-holding");

        var loser = await StartLabAsync(key);
        await RunAsync(Workflow, loser, "rl-acquire");
        await WaitUntilSettledAsync(Workflow, loser);

        var (loserState, loserStatus) = await GetInstanceStateAsync(Workflow, loser);
        Assert.Equal("rl-waiting", loserState);
        Assert.NotEqual("F", loserStatus);

        var (holderState, holderStatus) = await GetInstanceStateAsync(Workflow, holder);
        Assert.Equal("rl-holding", holderState);
        Assert.NotEqual("F", holderStatus);
    }

    /// <summary>
    /// Two instances on different keys both hold at the same time. Without this, a lock that
    /// serialized everything — a constant key, or a key resolution that silently collapsed to one
    /// value — would pass every contention test above while quietly halving throughput.
    /// </summary>
    [Fact]
    public async Task DifferentKeys_DoNotContend()
    {
        var first = await StartLabAsync(NewKey());
        var second = await StartLabAsync(NewKey());

        await RunAcceptedAsync(Workflow, first, "rl-acquire");
        await RunAcceptedAsync(Workflow, second, "rl-acquire");

        await WaitForInstanceStateAsync(Workflow, first, "rl-holding");
        await WaitForInstanceStateAsync(Workflow, second, "rl-holding");
    }

    /// <summary>
    /// Extend keeps a lease alive past the TTL it was taken with.
    /// <para>
    /// The holder acquires with a 5-second TTL and immediately extends to 300. The contender then
    /// waits out the original TTL and still loses the race: without a working Extend the lease would
    /// have expired and the contender would be holding. Dapr's lock has no extend, so this runs only
    /// when the runtime is on the Postgres lease provider — set
    /// <c>VNEXT_RESOURCE_LOCK_PROVIDER=postgres</c> to match a runtime started with
    /// <c>ResourceLock:Provider=postgres</c>.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Extend_KeepsTheLeasePastItsOriginalTtl()
    {
        Skip.IfNot(
            ProviderIsPostgres,
            "Extend is only implemented by the Postgres lease provider; set " +
            "VNEXT_RESOURCE_LOCK_PROVIDER=postgres when the runtime runs with it.");

        var key = NewKey();

        var holder = await StartLabAsync(key);
        await RunAcceptedAsync(Workflow, holder, "rl-acquire-short");
        await WaitForInstanceStateAsync(Workflow, holder, "rl-holding-short");

        await RunAcceptedAsync(Workflow, holder, "rl-extend");
        await WaitForInstanceStateAsync(Workflow, holder, "rl-holding-extended");

        // Outlive the TTL the lock was originally taken with, with margin.
        await Task.Delay(ShortTtl + TimeSpan.FromSeconds(3));

        var contender = await StartLabAsync(key);
        await RunAsync(Workflow, contender, "rl-acquire");
        await WaitUntilSettledAsync(Workflow, contender);

        var (contenderState, _) = await GetInstanceStateAsync(Workflow, contender);
        Assert.Equal("rl-waiting", contenderState);

        var (holderState, holderStatus) = await GetInstanceStateAsync(Workflow, holder);
        Assert.Equal("rl-holding-extended", holderState);
        Assert.NotEqual("F", holderStatus);
    }

    /// <summary>
    /// Starts an instance parked on <c>rl-idle</c> carrying the key its acquiring transitions will
    /// resolve.
    /// </summary>
    private async Task<string> StartLabAsync(string lockKey)
    {
        var instanceId = await StartAsync(Workflow, new { lockKey });
        await WaitUntilSettledAsync(Workflow, instanceId);
        return instanceId;
    }

    /// <summary>A key no other run of this suite can collide with.</summary>
    private static string NewKey() => $"k-{Guid.NewGuid():N}";
}
