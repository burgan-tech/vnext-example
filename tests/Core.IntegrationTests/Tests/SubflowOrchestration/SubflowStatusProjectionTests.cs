using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowOrchestration;

/// <summary>
/// What a polling client observes on a parent whose chain is working: the STATUS half of the
/// projection, and the conditional GET that carries it.
/// <para>
/// A parent holding an open SubFlow correlation is Busy for that subflow's whole lifetime by
/// design, so its own row never moves when the chain below it does. An accept that reserves the
/// chain flips only the LEAF's status. Until <c>Instance.EffectiveStatus</c> existed, nothing the
/// accept wrote was visible to the parent's state fingerprint — the cached state response stayed
/// valid across exactly the transition it must not survive, and a client polling right after its
/// 202 was told the flow was Active, followed the stale body's view link and got a 404. Measured in
/// preprod on 2026-09-10 (trace <c>0dbc92d9…</c>): a body built 142 seconds earlier.
/// </para>
/// <para>
/// The release is the other half and it does not travel by walk: a child leaving Busy at its own
/// rest point reaches the ancestors as a notification. When the episode ends in the state it
/// started in — a <c>$self</c> shared transition — there was nothing to notify with at all, and
/// every ancestor stayed Busy with no later event to correct it.
/// </para>
/// <para>
/// These assert what a CLIENT sees (state function status, ETag, transition list), never the
/// column: <c>EffectiveStatus</c> is fingerprint material and is deliberately not served.
/// </para>
/// </summary>
public class SubflowStatusProjectionTests : WorkflowTestBase
{
    private const string Parent = "subflow-orchestration-parent";
    private const string Child = "subflow-orchestration-child";
    private const int Threshold = 3;

    public SubflowStatusProjectionTests(VNextTestEnvironment environment) : base(environment) { }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives a fresh parent to the point where the chain is at rest in the child's manual state —
    /// the shape every test here starts from: parent Busy on an open correlation, child Active and
    /// waiting for a human.
    /// </summary>
    private async Task<string> StartAndRestInTheChildAsync(string tag)
    {
        var parentId = await StartAsync(Parent,
            new { testId = $"{tag}-{Guid.NewGuid():N}"[..24], updateThreshold = Threshold });

        await WaitForInstanceStateAsync(Parent, parentId, "parent-collect");

        var accepted = 0;
        await WaitUntilAsync(async () =>
        {
            if (accepted < Threshold)
            {
                var status = await RunAsync(Parent, parentId, "update-parent-progress",
                    new { updateNonce = Guid.NewGuid().ToString("N")[..8] });
                if ((int)status < 400) accepted++;
                await Task.Delay(200);
            }

            return (await GetInstanceStateAsync(Parent, parentId)).State == "parent-subflow-state";
        }, $"the collect gate never fired after {Threshold} accepted updates", TimeSpan.FromSeconds(90));

        await WaitForObservedStateAsync(Parent, parentId, "child-manual-state");
        await WaitForObservedStatusAsync(parentId, "A");
        return parentId;
    }

    /// <summary>
    /// Accepts an async transition on the parent and asserts only that it was accepted.
    /// Deliberately not the base class's accept-and-settle helper: that waits for the addressed
    /// instance to leave Busy, and a parent holding an open SubFlow correlation is Busy for the
    /// subflow's entire lifetime by design — the settle signal for a chain is the OBSERVED state
    /// and status, which is what every test here waits on instead.
    /// </summary>
    private async Task AcceptAsync(string parentId, string transitionKey, object? body = null)
    {
        var (status, responseBody) = await SendRawAsync(
            HttpMethod.Patch,
            $"api/v1/core/workflows/{Parent}/instances/{parentId}/transitions/{transitionKey}?sync=false",
            body ?? new { },
            Headers());

        Assert.True((int)status < 400, $"'{transitionKey}' was refused with {(int)status}: {responseBody}");
    }

    /// <summary>The status a polling client reads off the parent — the deepest active level's.</summary>
    private async Task<string> GetObservedStatusAsync(string parentId) =>
        (await GetObservedStateAsync(Parent, parentId)).Status;

    private Task WaitForObservedStatusAsync(string parentId, string status, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () => await GetObservedStatusAsync(parentId) == status,
            $"the observed status never became '{status}'",
            timeout ?? TimeSpan.FromSeconds(60));

    /// <summary>A warm cache entry is the precondition, not an accident: the defect only shows when
    /// a response built before the accept is still sitting in the state-function cache.</summary>
    private async Task<string> WarmTheCacheAndTakeTheEtagAsync(string parentId)
    {
        var (status, etag, _) = await PollStateAsync(Parent, parentId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrEmpty(etag), "the state function answered without an ETag");
        return etag!;
    }

    private static JsonElement Parse(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static IReadOnlyList<string> TransitionNames(JsonElement state) =>
        state.TryGetProperty("transitions", out var transitions)
            ? transitions.EnumerateArray()
                .Select(t => t.GetProperty("name").GetString() ?? "")
                .ToList()
            : [];

    // ── the reported defect ──────────────────────────────────────────────────

    /// <summary>
    /// THE regression test. The accept reserves the chain down to the leaf before it answers 202,
    /// so the very next poll must say Busy — and must not still be offering the transition it just
    /// accepted. The client polls within milliseconds of its own 202; there is no window in which
    /// "Active" is a true answer.
    /// </summary>
    [Fact]
    public async Task ThePollRightAfterAn202_SeesBusy_AndNoLongerOffersTheAcceptedTransition()
    {
        var parentId = await StartAndRestInTheChildAsync("busy-now");
        await WarmTheCacheAndTakeTheEtagAsync(parentId);

        var accepted = await RunAsync(Parent, parentId, "proceed-to-subflow");
        Assert.True((int)accepted < 400, $"the transition was refused with {(int)accepted}");

        var (status, _, body) = await PollStateAsync(Parent, parentId);

        Assert.Equal(HttpStatusCode.OK, status);
        var state = Parse(body);
        Assert.Equal("B", state.GetProperty("status").GetString());
        Assert.DoesNotContain("proceed-to-subflow", TransitionNames(state));
    }

    /// <summary>
    /// The same moment, seen the way a long-polling client actually sees it: holding the ETag it
    /// got while the chain was idle. A 304 here is the failure — it is the runtime saying "nothing
    /// changed" about the transition the client itself just started.
    /// </summary>
    [Fact]
    public async Task AClientHoldingTheIdleEtag_IsNotToldNotModified_AfterItAcceptsATransition()
    {
        var parentId = await StartAndRestInTheChildAsync("etag-busy");
        var idleEtag = await WarmTheCacheAndTakeTheEtagAsync(parentId);

        // Idle means idle: the same ETag answers 304 while nothing is happening.
        var (idleStatus, _, _) = await PollStateAsync(Parent, parentId, idleEtag);
        Assert.Equal(HttpStatusCode.NotModified, idleStatus);

        var accepted = await RunAsync(Parent, parentId, "proceed-to-subflow");
        Assert.True((int)accepted < 400, $"the transition was refused with {(int)accepted}");

        var (status, _, body) = await PollStateAsync(Parent, parentId, idleEtag);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("B", Parse(body).GetProperty("status").GetString());
    }

    // ── the release, including the episode that changes no state ─────────────

    /// <summary>
    /// The chain must come back on its own. The child moves into its own SubFlow state and the
    /// grandchild comes to rest, so what the client observes is the grandchild's state and Active
    /// again — two levels below the instance being polled.
    /// </summary>
    [Fact]
    public async Task TheChainReturnsToActive_WhenTheDescendantReachesItsNextRestPoint()
    {
        var parentId = await StartAndRestInTheChildAsync("release");

        await AcceptAsync(parentId, "proceed-to-subflow");

        await WaitForObservedStateAsync(Parent, parentId, "grandchild-initial", timeout: TimeSpan.FromSeconds(60));
        await WaitForObservedStatusAsync(parentId, "A");

        var (state, status) = await GetObservedStateAsync(Parent, parentId);
        Assert.Equal("grandchild-initial", state);
        Assert.Equal("A", status);
    }

    /// <summary>
    /// The case that does not heal itself. <c>shared-child-mark</c> targets <c>$self</c>: the child
    /// runs the full lifecycle and comes to rest in the state it started in. The accept stamped the
    /// whole chain Busy on the way down, and a notification triggered by a state change alone has
    /// nothing to report — so before the status became a second reason to publish, the parent sat
    /// at Busy for good and a client long-polling it waited on a chain that had already finished.
    /// </summary>
    [Fact]
    public async Task AnEpisodeThatChangesNoState_StillReleasesTheChain()
    {
        var parentId = await StartAndRestInTheChildAsync("self-release");

        await AcceptAsync(parentId, "shared-child-mark");

        // Back to Active while the observed STATE never moved — the status is the only signal.
        await WaitForObservedStatusAsync(parentId, "A", TimeSpan.FromSeconds(45));

        var (state, status) = await GetObservedStateAsync(Parent, parentId);
        Assert.Equal("child-manual-state", state);
        Assert.Equal("A", status);
    }

    /// <summary>
    /// The same episode from the long-poller's seat: a client that went to sleep on the idle ETag
    /// has to be woken when the chain comes back, even though the state it is shown is the one it
    /// already had.
    /// </summary>
    [Fact]
    public async Task AStatusOnlyEpisode_MovesTheEtagForALongPoller()
    {
        var parentId = await StartAndRestInTheChildAsync("self-etag");
        var idleEtag = await WarmTheCacheAndTakeTheEtagAsync(parentId);

        await AcceptAsync(parentId, "shared-child-mark");
        await WaitForObservedStatusAsync(parentId, "A", TimeSpan.FromSeconds(45));

        var (status, _, body) = await PollStateAsync(Parent, parentId, idleEtag);

        Assert.Equal(HttpStatusCode.OK, status);
        var state = Parse(body);
        Assert.Equal("child-manual-state", state.GetProperty("state").GetString());
        Assert.Equal("A", state.GetProperty("status").GetString());
    }

    // ── a descendant's state change, and completion ──────────────────────────

    /// <summary>
    /// The state dimension of the same conditional GET: a descendant two levels down moving to a
    /// new state must invalidate the poller's ETag.
    /// </summary>
    [Fact]
    public async Task ADescendantsStateChange_MovesTheEtag()
    {
        var parentId = await StartAndRestInTheChildAsync("etag-state");
        var idleEtag = await WarmTheCacheAndTakeTheEtagAsync(parentId);

        await AcceptAsync(parentId, "proceed-to-subflow");
        await WaitForObservedStateAsync(Parent, parentId, "grandchild-initial", timeout: TimeSpan.FromSeconds(60));

        var (status, _, body) = await PollStateAsync(Parent, parentId, idleEtag);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("grandchild-initial", Parse(body).GetProperty("state").GetString());
    }

    /// <summary>
    /// Completion, end to end: the grandchild finishes, the child resumes and finishes, the parent
    /// resumes and reaches its own finish state. Once the correlations close, what the client
    /// observes is the parent again — its own state and a terminal status — and the ETag it was
    /// holding is gone.
    /// </summary>
    [Fact]
    public async Task WhenTheChainCompletes_TheClientObservesTheParentsOwnTerminalState()
    {
        var parentId = await StartAndRestInTheChildAsync("completion");

        await AcceptAsync(parentId, "proceed-to-subflow");
        await WaitForObservedStateAsync(Parent, parentId, "grandchild-initial", timeout: TimeSpan.FromSeconds(60));
        await WaitForObservedStatusAsync(parentId, "A");

        var beforeCompletion = await WarmTheCacheAndTakeTheEtagAsync(parentId);

        await AcceptAsync(parentId, "complete-grandchild");

        await WaitUntilAsync(
            async () => TerminalStatuses.Contains((await GetInstanceStateAsync(Parent, parentId)).Status),
            "the parent never reached a terminal status after the chain completed",
            TimeSpan.FromSeconds(90));

        var (state, status) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("parent-completed", state);
        Assert.Equal("C", status);

        var (pollStatus, _, body) = await PollStateAsync(Parent, parentId, beforeCompletion);
        Assert.Equal(HttpStatusCode.OK, pollStatus);
        Assert.Equal("C", Parse(body).GetProperty("status").GetString());
    }

    // ── the interaction signal, seen from the level the client polls ─────────

    /// <summary>
    /// The client polls the PARENT and never learns which level it is talking to — so an
    /// interaction declared deep in the chain has to arrive in the parent's own body, with an ack
    /// href addressed to the instance the client is holding. The signal is also independent of the
    /// status: the child pauses in Busy while the acknowledgement is pending, and "busy" is exactly
    /// when the client must stop polling and render, not when it should keep waiting.
    /// </summary>
    [Fact]
    public async Task TheChildsInteractionSignal_ArrivesOnTheParentsPoll_WithAnAckHrefForTheParent()
    {
        var parentId = await StartAndRestInTheChildAsync("interaction");
        await WarmTheCacheAndTakeTheEtagAsync(parentId);

        await AcceptAsync(parentId, "enter-interaction");
        await WaitForObservedStateAsync(Parent, parentId, "child-interaction-state");

        var (status, _, body) = await PollStateAsync(Parent, parentId);
        Assert.Equal(HttpStatusCode.OK, status);
        var state = Parse(body);

        var interaction = state.GetProperty("interaction");
        Assert.True(interaction.GetProperty("terminateLongPoll").GetBoolean(),
            "the child's long-poll termination signal never reached the parent's body");
        Assert.Equal(10, interaction.GetProperty("fallbackTimeoutSeconds").GetInt32());

        // Addressed to the instance being polled, not to the child that declared it.
        var ackHref = interaction.GetProperty("ack").GetProperty("href").GetString() ?? "";
        Assert.Contains(parentId, ackHref);
        Assert.EndsWith("/longpoll/ack", ackHref);

        // Independent of status: the pause holds the chain Busy and the signal stands anyway.
        Assert.Equal("child-interaction-state", state.GetProperty("state").GetString());
        Assert.Equal("B", state.GetProperty("status").GetString());
    }

    /// <summary>
    /// The same signal for a client that is actually long-polling — asleep on the ETag it took
    /// while the chain was idle. If that poll answered 304, the client would keep waiting through
    /// the very state whose whole purpose is to tell it to stop.
    /// </summary>
    [Fact]
    public async Task ALongPollerHoldingTheIdleEtag_IsWokenByTheInteractionState()
    {
        var parentId = await StartAndRestInTheChildAsync("interaction-etag");
        var idleEtag = await WarmTheCacheAndTakeTheEtagAsync(parentId);

        await AcceptAsync(parentId, "enter-interaction");
        await WaitForObservedStateAsync(Parent, parentId, "child-interaction-state");

        var (status, _, body) = await PollStateAsync(Parent, parentId, idleEtag);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Parse(body).GetProperty("interaction").GetProperty("terminateLongPoll").GetBoolean());
    }

    /// <summary>
    /// Acknowledging on the parent — the only instance the client knows — resumes the paused
    /// pipeline two levels down and the chain comes back to Active without leaving the state.
    /// </summary>
    [Fact]
    public async Task AcknowledgingOnTheParent_ResumesThePausedChild()
    {
        var parentId = await StartAndRestInTheChildAsync("interaction-ack");
        await AcceptAsync(parentId, "enter-interaction");
        await WaitForObservedStateAsync(Parent, parentId, "child-interaction-state");

        var (_, _, body) = await PollStateAsync(Parent, parentId);
        var ackHref = Parse(body).GetProperty("interaction").GetProperty("ack")
            .GetProperty("href").GetString()!;

        var (ackStatus, ackBody) = await SendRawAsync(HttpMethod.Post, ToAbsolute(ackHref), null, Headers());
        Assert.True((int)ackStatus < 400, $"the acknowledge was refused with {(int)ackStatus}: {ackBody}");

        await WaitForObservedStatusAsync(parentId, "A", TimeSpan.FromSeconds(30));
        Assert.Equal("child-interaction-state", (await GetObservedStateAsync(Parent, parentId)).State);
    }

    /// <summary>
    /// And when nobody acknowledges, the declared fallback window resumes it anyway — the pause is
    /// a courtesy to the client, never a way for a client that walked away to strand the chain.
    /// </summary>
    [Fact]
    public async Task TheFallbackWindow_ResumesTheChain_WhenNobodyAcknowledges()
    {
        var parentId = await StartAndRestInTheChildAsync("interaction-fallback");
        await AcceptAsync(parentId, "enter-interaction");
        await WaitForObservedStateAsync(Parent, parentId, "child-interaction-state");

        // fallbackTimeoutSeconds is 10 on this state; allow generous slack for the scheduler.
        await WaitForObservedStatusAsync(parentId, "A", TimeSpan.FromSeconds(60));

        Assert.Equal("child-interaction-state", (await GetObservedStateAsync(Parent, parentId)).State);
    }

    /// <summary>Hrefs in the body are api-version-relative; the raw client needs the full path.</summary>
    private static string ToAbsolute(string href)
    {
        var trimmed = href.TrimStart('/');
        return trimmed.StartsWith("api/", StringComparison.Ordinal) ? trimmed : $"api/v1/{trimmed}";
    }
}
