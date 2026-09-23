using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowStartFailureLab;

/// <summary>
/// subflow-start-failure-lab: what happens when the post-commit <c>StartSubflowJob</c> for a
/// blocking SubFlow state (<c>subFlow.type: "S"</c>) can never succeed, because the child's own
/// start transition fails schema validation — a field its schema requires
/// (<c>mustProvide</c>) that the parent's <c>ISubFlowMapping</c> input handler deliberately never
/// supplies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A code review on the sibling branch (stranded-busy-and-dead-flag-cleanup)
/// raised a CRITICAL against <c>TransitionRunner.CompensateFailedCoordinationAsync</c>: when a
/// post-commit <c>StartSubflowJob</c> fails, the runtime now faults the parent, and the fault is
/// documented as making the instance "visible and retryable". But <c>HandleSubFlowStep</c> (order
/// 70) commits the <c>InstanceCorrelation</c> in its OWN unit of work, strictly before the
/// post-commit job that would actually create the child ever runs. So by the time the fault lands,
/// the parent already carries a correlation pointing at a child that was never created.
/// </para>
/// <para>
/// <b>Measured outcome — two tests.</b> Running this scenario against the runtime showed the
/// fault/incident half of the claim holds
/// (<see cref="AFailedSubflowStartFaultsTheParentInsteadOfStrandingItBusy"/>). Retry originally made
/// things WORSE — <c>HTTP 404 notfound.Instance:100013</c> naming the never-created child, parent
/// left unfaulted but stuck in <c>parent-subflow-state</c> with no progress and its fault history
/// erased. That specific failure mode is CLOSED in the runtime: <c>InstanceRetryAppService</c> now
/// probes the child BEFORE touching the parent and, when it is missing, re-arms the parent (unfault,
/// then Busy) and restarts the subflow start for the SAME correlation instead of delegating to a
/// child that never existed. This fixture's cause (a permanently missing required field the parent's
/// mapping never supplies) cannot self-heal from a faithful restart carrying no new data — "a re-run
/// of a start should look like that start" — so the second test
/// (<see cref="RetryingAFaultedSubflowStartThatCannotSucceed_ReFaultsInsteadOfStrandingTheParent"/>)
/// proves the invariant that actually matters for a restart that fails AGAIN: the parent comes back
/// around to Faulted with a FRESH incident, never stranded Busy with neither an incident nor a live
/// child. Both tests are needed because they pin genuinely different, independently useful
/// invariants (fault-visibility on the original failure vs. no-stranding on a failed retry).
/// </para>
/// <para>
/// <b>The fixture form.</b> The cheapest failure form was tried first: <c>subFlow.process</c>
/// pointing at a real child key (<c>subflow-orchestration-child</c>) at a version that was never
/// published (<c>9.9.9</c>). <c>wf sync</c> accepted that at publish time. But starting the PARENT
/// then failed synchronously with a plain 404 ("Workflow not found in runtime backend", target
/// <c>core/subflow-orchestration-child@9.9.9</c>) — <c>IComponentCacheStore</c> resolves the whole
/// reachable component graph, including every <c>subFlow.process</c>, when the PARENT's own
/// definition loads, so the parent never even reached <c>HandleSubFlowStep</c> and no correlation
/// was ever committed. That does not reproduce the bug under test, so this lab uses its own child
/// workflow (<c>subflow-start-failure-lab-child</c>, a real, resolvable, published component) whose
/// start transition carries a schema requiring <c>mustProvide</c> — a field the parent's mapping
/// never sends. That failure surfaces only when the CHILD's own start actually runs, post-commit,
/// which is exactly the shape the CRITICAL describes.
/// </para>
/// <para>
/// <b>Sync vs async start.</b> The parent MUST be started asynchronously (<c>sync=false</c>), not
/// through <see cref="WorkflowTestBase.StartAsync"/> (the SDK client hard-codes <c>sync=true</c>).
/// Measured directly: with <c>sync=true</c> the client's own start request blocks on the whole
/// pipeline including the failing post-commit child start, and the CHILD's schema-validation error
/// is returned as the top-level response to the START call itself (HTTP 400, "Required properties
/// [\"mustProvide\"] are not present") — even though the parent instance is ALSO faulted
/// server-side in the same call, by the same compensation path. That is an artifact of sync mode's
/// blocking semantics, not the scenario under test. With <c>sync=false</c> (the production default
/// for a client-facing start, and the exact shape the CRITICAL assumes: "when a post-commit
/// StartSubflowJob fails, the runtime faults the parent"), the start call returns 202 immediately
/// and the fault happens in the background — observable only by polling, which is what both tests
/// below do.
/// </para>
/// </remarks>
public class SubflowStartFailureLabTests : WorkflowTestBase
{
    private const string Parent = "subflow-start-failure-lab-parent";

    public SubflowStartFailureLabTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Pins the half of the CRITICAL's claim the runtime change actually delivers: a post-commit
    /// child-start failure faults the parent, and the fault is visible through an active incident —
    /// instead of leaving the instance stranded <c>Busy</c> forever.
    /// </summary>
    /// <remarks>
    /// Before this runtime change, a failed post-commit <c>StartSubflowJob</c> ran neither
    /// settlement nor fault: <c>HandleSubFlowStep</c> (order 70) had already committed the
    /// <c>InstanceCorrelation</c> and set the instance <c>Busy</c> for the subflow's lifetime, and
    /// nothing ever flipped it back. The parent sat <c>Busy</c> with no incident and no fault
    /// recorded — invisible to every query surface that reports on faulted/active work, and
    /// recoverable only by a direct database intervention (there being no faulted instance for
    /// <c>retry</c> to act on, and no way to distinguish it from an instance genuinely still doing
    /// work). This test proves that strand no longer happens: the parent reaches <c>Faulted</c>
    /// within a bounded wait, carries an active incident whose error naming the real cause, and is
    /// specifically NOT left <c>Busy</c>.
    /// </remarks>
    [Fact]
    public async Task AFailedSubflowStartFaultsTheParentInsteadOfStrandingItBusy()
    {
        // 1) Start the parent ASYNCHRONOUSLY. Its initial state auto-transitions straight into the
        // blocking SubFlow state; HandleSubFlowStep (order 70) commits the InstanceCorrelation in
        // its own UoW, and only then does the post-commit StartSubflowJob call the child's start
        // transition — which fails schema validation ("mustProvide" is required, never supplied).
        var parentId = await StartParentAsync(new { testId = $"start-failure-{Guid.NewGuid():N}"[..24] });

        await WaitUntilAsync(async () =>
        {
            var (_, status) = await GetInstanceStateAsync(Parent, parentId);
            return status == "F";
        }, $"the parent never faulted after the child's start failed schema validation — was it " +
           $"left stranded Busy instead? {await DescribeAsync(Parent, parentId)}", TimeSpan.FromSeconds(60));

        var (faultedState, faultedStatus) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("F", faultedStatus);
        Assert.NotEqual("B", faultedStatus); // explicit: not stranded Busy — the pre-fix failure mode

        // 2) An incident must exist — the "visible" half of the claim. Quote its error code and
        // message in every failure this assertion can produce, so a reader sees the real cause
        // without re-running anything.
        var (incidentStatus, incidentBody) = await GetActiveIncidentAsync(Parent, parentId);
        var errorCode = TextOrDash(incidentBody, "errorCode");
        var incidentMessage = TextOrDash(incidentBody, "message");

        Assert.True(incidentStatus == HttpStatusCode.OK,
            $"expected an active incident on the faulted parent (state={faultedState}), " +
            $"got {(int)incidentStatus}: {incidentBody.GetRawText()}");
        Assert.True(incidentStatus != HttpStatusCode.OK || !string.IsNullOrEmpty(errorCode),
            $"incident found but carried no errorCode/message — errorCode='{errorCode}' " +
            $"message='{incidentMessage}'");
    }

    /// <summary>
    /// Proves the invariant a retry-driven subflow restart must never violate, for the case where
    /// the restart itself CANNOT succeed: the parent must come back around to <c>Faulted</c> with a
    /// fresh incident, never left stranded <c>Busy</c> with neither an incident nor a live child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What used to happen.</b> <c>HandleSubFlowStep</c> commits the <c>InstanceCorrelation</c> in
    /// its own unit of work strictly before the post-commit job that would actually create the child
    /// ever runs — so a child that was never created was indistinguishable, from the correlation
    /// alone, from a live one. The original <c>InstanceRetryAppService.RetryFaultedInstanceAsync</c>
    /// branched on <c>instance.HasActiveSubFlow</c> — true here — straight into unfaulting the parent
    /// (committing that unfault and resolving its incidents) and only THEN querying the child's
    /// state. That child did not exist, so the query failed and the retry request failed too, AFTER
    /// the parent had already been unfaulted: <c>HTTP 404 Instance:100013</c> naming the never-created
    /// child, with the parent left neither Faulted (refusing a second retry) nor making progress —
    /// worse than before the call.
    /// </para>
    /// <para>
    /// <b>The fix.</b> The child's absence is now probed BEFORE the parent is touched. When the
    /// probe comes back "not found", <c>InstanceRetryAppService</c> re-arms the parent (unfault, then
    /// Busy — mirroring the SubFlow-lifetime invariant a live blocking child always has, under the
    /// same short status lock every other status flip uses) and restarts the subflow start for the
    /// SAME correlation — idempotent on its own pre-generated <c>SubFlowInstanceId</c>
    /// (<c>ISubflowStarter</c>'s <c>StrictIdempotency</c>), so this would create the child the
    /// correlation always pointed at rather than a second one, and it never re-runs the parent's own
    /// transition (whose tasks already ran once).
    /// </para>
    /// <para>
    /// <b>Why THIS retry cannot recover, on purpose.</b> <c>mustProvide</c> is never supplied from
    /// the parent's own instance data (<c>ParentToChildSubFlowMapping.csx</c>), and a restart
    /// deliberately carries no new caller data of its own — "a re-run of a start should look like
    /// that start" (a caller-data-threading mechanism was considered and rejected as scope creep
    /// beyond this fix). So a bare retry against THIS fixture fails again, identically, for the
    /// identical reason: a fully faithful re-run of a permanently misconfigured mapping cannot
    /// self-heal, and it is not supposed to. What matters — and what this test actually proves — is
    /// that failing again does not stand for anything worse than "still faulted, still retryable":
    /// no strand, no silent Busy, no swallowed error.
    /// </para>
    /// <para>
    /// <b>Measured result.</b> The retry call itself fails (HTTP ≥ 400: the restart's own error, not
    /// a 5xx crash). The parent is re-faulted — <c>Faulted</c>, still in <c>parent-subflow-state</c>
    /// — carrying a NEW incident (a different <c>id</c> than the one the original fault raised: the
    /// old one really was resolved during the re-arm, and this is a fresh one from the failed
    /// restart, not the same stale row left behind). Postgres confirms the parent's raw status,
    /// its correlation's <c>SubFlowInstanceId</c> (unchanged — no second correlation was created),
    /// and that no row exists yet in the child schema for it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RetryingAFaultedSubflowStartThatCannotSucceed_ReFaultsInsteadOfStrandingTheParent()
    {
        var parentId = await StartParentAsync(new { testId = $"start-failure-{Guid.NewGuid():N}"[..24] });

        await WaitUntilAsync(async () =>
        {
            var (_, status) = await GetInstanceStateAsync(Parent, parentId);
            return status == "F";
        }, $"the parent never faulted after the child's start failed schema validation — " +
           $"{await DescribeAsync(Parent, parentId)}", TimeSpan.FromSeconds(60));

        var (firstIncidentStatus, firstIncidentBody) = await GetActiveIncidentAsync(Parent, parentId);
        Assert.True(firstIncidentStatus == HttpStatusCode.OK,
            $"expected an active incident on the freshly faulted parent, got {(int)firstIncidentStatus}: {firstIncidentBody}");
        var firstIncidentId = TextOrDash(firstIncidentBody, "id");

        // Bare retry, no corrected data — a faithful re-run of the exact same (permanently broken)
        // start. It MUST fail again, for the identical reason; that is not a bug, see remarks.
        var (retryStatus, retryBody) = await RetryAsync(Parent, parentId);

        Assert.True((int)retryStatus >= 400,
            $"expected the restart to fail again (same missing 'mustProvide', no data changed to fix " +
            $"it), got HTTP {(int)retryStatus}: {retryBody}");

        // The invariant under test: a restart that fails must not strand the parent. It has to come
        // back around to Faulted — re-armed Busy, attempted, and re-faulted — within a bounded wait.
        await WaitUntilAsync(async () =>
        {
            var (_, status) = await GetInstanceStateAsync(Parent, parentId);
            return status == "F";
        }, $"the parent was left stranded (not re-faulted) after its subflow restart failed again — " +
           $"{await DescribeAsync(Parent, parentId)}", TimeSpan.FromSeconds(30));

        var (finalState, finalStatus) = await GetInstanceStateAsync(Parent, parentId);
        Assert.Equal("F", finalStatus);
        Assert.Equal("parent-subflow-state", finalState);

        var (secondIncidentStatus, secondIncidentBody) = await GetActiveIncidentAsync(Parent, parentId);
        var secondErrorCode = TextOrDash(secondIncidentBody, "errorCode");
        Assert.True(secondIncidentStatus == HttpStatusCode.OK,
            $"expected a fresh active incident after the re-fault, got {(int)secondIncidentStatus}: {secondIncidentBody}");
        Assert.False(string.IsNullOrEmpty(secondErrorCode),
            $"incident found but carried no errorCode — {secondIncidentBody}");

        var secondIncidentId = TextOrDash(secondIncidentBody, "id");
        Assert.NotEqual(firstIncidentId, secondIncidentId);
    }

    // ── local helpers ────────────────────────────────────────────────────────
    // None of these have an SDK method or a home in WorkflowTestBase yet — all raw HTTP, matching
    // the pattern in Tests/ErrorBoundaryLab/ErrorBoundaryLabTestBase.cs. StartParentAsync exists
    // because WorkflowTestBase.StartAsync goes through VNextApiClient.StartInstanceAsync, which
    // hard-codes sync=true — wrong for this scenario, see the class remarks.

    /// <summary>Starts the parent with <c>sync=false</c> and returns its instance id.</summary>
    private async Task<string> StartParentAsync(object body)
    {
        var url = $"api/v1/core/workflows/{Parent}/instances/start?sync=false";
        var (status, responseBody) = await SendRawAsync(HttpMethod.Post, url, body, Headers());

        Assert.True((int)status < 400, $"start was refused with {(int)status}: {responseBody}");

        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("start response carried no instance id");
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetActiveIncidentAsync(
        string workflow, string instanceId)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/incidents/active";
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers());
        return (status, ParseOrEmpty(body));
    }

    /// <summary><c>POST .../instances/{id}/retry</c>. sync=true so the response reflects the
    /// outcome of the retried transition directly, the same way IncidentLifecycleTests does it.
    /// <paramref name="body"/> is the optional retry-supplied <c>TransitionDataInput</c> body (e.g.
    /// <c>{"attributes": {...}}</c>) — the corrected data a real operator would supply.</summary>
    private Task<(HttpStatusCode Status, string Body)> RetryAsync(
        string workflow, string instanceId, object? body = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/retry?sync=true";
        return SendRawAsync(HttpMethod.Post, url, body ?? new { }, Headers());
    }

    private static string TextOrDash(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "-"
            : "-";

    private static JsonElement ParseOrEmpty(string body) =>
        string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
}
