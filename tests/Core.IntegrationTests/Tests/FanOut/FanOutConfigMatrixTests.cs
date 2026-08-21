using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FanOut;

/// <summary>
/// The <c>fan-out-config-matrix</c> flow — end-to-end coverage of <c>FanOutTask</c>'s
/// CONFIGURABLE surface (TaskType 21, inline mode). The sibling <c>FanOutDocumentsTests</c> pins
/// the happy path, the partial-failure branch and the single-write invariant for ONE configuration
/// (<c>allSettled</c>); nothing before this class exercised what happens when the configuration
/// changes.
/// <para>
/// <b>How a join verdict is observed — and how NOT to.</b> Each case state runs exactly one onEntry
/// task (that case's batch) and carries an unconditional auto transition to <c>case-settled</c>; the
/// workflow carries one global <c>rollback</c> error boundary routing to <c>case-failed</c>. So:
/// join succeeded ⇒ <c>case-settled</c>, join failed ⇒ <c>case-failed</c>. BOTH are asserted
/// positively.
/// </para>
/// <para>
/// The first revision of this class inferred a failed join from the instance FAULTING, with no
/// boundary declared. That was measured and is wrong: with no boundary configured a failing onEntry
/// task is not acted on at all and the auto transition fires regardless. The control that settled it
/// — feeding <c>documents</c> a string instead of an array, which makes <c>FanOutItemsResolver</c>
/// throw a hard <c>Result.Fail</c> — still reached <c>case-settled</c>. Every failed-join case
/// silently passed. Never infer failure from the absence of success in this flow.
/// </para>
/// <para>
/// <b>Every shape asserted here is produced by the RUNTIME.</b> <c>FanOutCaseMapping</c> overrides
/// <c>ItemInputHandler</c> only and deliberately leaves <c>OutputHandler</c> unoverridden, so
/// <c>caseResults</c> / <c>caseResultsSummary</c> are the executor's own <c>BuildDefaultOutput</c>.
/// </para>
/// <para>
/// <b>Known-red tests document filed defects.</b> Three tests are expected to fail against the
/// current runtime and are deliberately NOT weakened — two of them for the SAME defect (F1), which
/// is exactly why they stay separate: they pin two different cancellation causes.
/// <see cref="ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut"/> and
/// <see cref="EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode"/> both show that
/// an item cancelled while IN FLIGHT reports the inner task's raw
/// <c>Task:Unknown:{taskKey}:TaskCanceledException</c> instead of the fan-out cause;
/// <see cref="DurableMode_IsRefusedWithAnActionableValidationError"/> is F2. All are written up in
/// <c>docs/fanout-configurable-surface-findings.md</c>. Going green means the defect was fixed.
/// </para>
/// <para>
/// <b>Environment dependency — and the bug it hid.</b> The item/batch-timeout and concurrency cases
/// need MockLab's straggler route to genuinely take ~1500ms, because <c>itemTimeoutSeconds</c> is a
/// whole number of seconds and the fast route answers in ~10ms. That route was originally authored
/// as <c>documents/process-slow</c> — and MockLab matches routes by PREFIX, so it sat permanently
/// shadowed behind <c>documents/process</c> and its <c>delayMs</c> never applied. It now lives under
/// a sibling segment (<c>slow-documents/process</c>).
/// <see cref="AssertStragglerRouteIsActuallySlowAsync"/> guards all three against a recurrence,
/// checking the response BODY as well as the elapsed time. It matters most on the concurrency
/// CONTROL arm, which passed vacuously while the delay was missing — instant items make every
/// ceiling equivalent.
/// </para>
/// </summary>
public class FanOutConfigMatrixTests : WorkflowTestBase
{
    private const string Workflow = "fan-out-config-matrix";
    private const string SettledState = "case-settled";
    private const string FailedState = "case-failed";
    private const string ResultKey = "caseResults";
    private const string SummaryKey = "caseResultsSummary";

    // FanOutErrorCodes — the task's PUBLIC contract (BBT.Workflow.Tasks.Executors.FanOutErrorCodes).
    private const string ItemTimeoutCode = "FanOut:ItemTimeout";
    private const string BatchTimeoutCode = "FanOut:BatchTimeout";
    private const string ItemCancelledCode = "FanOut:ItemCancelled";

    /// <summary>
    /// MockLab's deliberately delayed route, the delay its seed configures, and a marker unique to
    /// that mock's response body.
    /// <para>
    /// The route lives under a SIBLING segment (<c>slow-documents/process</c>) rather than as a
    /// suffix of the fast one (<c>documents/process-slow</c>) because MockLab matches routes by
    /// PREFIX — see <see cref="AssertStragglerRouteIsActuallySlowAsync"/>.
    /// </para>
    /// </summary>
    private const string StragglerUrl = "http://localhost:3001/api/fan-out/slow-documents/process?documentId=DOC-SLOW-PROBE";
    private const string StragglerBodyMarker = "\"slow\"";
    private static readonly TimeSpan ConfiguredStragglerDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The largest <c>itemTimeoutSeconds</c> any straggler-driven case configures. The mock's delay
    /// must EXCEED this or the item can never blow its deadline and the case is vacuous — which is
    /// the real floor the guard has to enforce, not merely "some delay was applied".
    /// </summary>
    private static readonly TimeSpan StragglerFloor = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(90);

    public FanOutConfigMatrixTests(VNextTestEnvironment environment) : base(environment) { }

    // ── join.policy: all ─────────────────────────────────────────────────────

    [Fact]
    public async Task JoinAll_EveryItemSucceeds_SettlesWithAFullResultSet()
    {
        var instanceId = await RunCaseAsync("run-join-all", "DOC-1", "DOC-2", "DOC-3");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 3, succeeded: 3, failed: 0);
        Assert.False(Summary(attributes).GetProperty("timedOut").GetBoolean());
    }

    [Fact]
    public async Task JoinAll_OneItemFails_FailsTheJoin()
    {
        // 'all' is the atomic policy: one failure is the batch's failure. Contrast with
        // FanOutDocumentsTests, where the same item mix under 'allSettled' branches instead.
        var instanceId = await RunCaseAsync("run-join-all", "DOC-1", "DOC-FAIL-A", "DOC-3");
        await WaitForFailedAsync(instanceId, "join policy 'all' must fail the batch on a failed item");

        // A failed join still lands its result set: FanOutTaskExecutor deliberately does NOT use
        // TaskInvocationResult.Failure, precisely so a caller can branch on WHICH items failed.
        AssertFailedJoinStillCarriedItsData(await GetAttributesAsync(Workflow, instanceId));
    }

    [Fact]
    public async Task JoinAll_EmptyCollection_SucceedsVacuously()
    {
        // Vacuous truth: with no items there is no item that failed. A real authoring hazard — a
        // batch over an empty list silently "succeeds" — so it is pinned deliberately.
        var instanceId = await RunCaseAsync("run-join-all");
        await WaitForSettledAsync(instanceId);

        AssertSummary(await GetAttributesAsync(Workflow, instanceId), total: 0, succeeded: 0, failed: 0);
    }

    // ── join.policy: allSettled ──────────────────────────────────────────────

    [Fact]
    public async Task JoinAllSettled_EmptyCollection_Succeeds()
    {
        var instanceId = await RunCaseAsync("run-join-all-settled");
        await WaitForSettledAsync(instanceId);

        AssertSummary(await GetAttributesAsync(Workflow, instanceId), total: 0, succeeded: 0, failed: 0);
    }

    // ── join.policy: quorum + join.minSuccess ────────────────────────────────

    [Fact]
    public async Task JoinQuorum_MinSuccessMet_SettlesEvenThoughItemsFailed()
    {
        // minSuccess = 2 on the component; exactly 2 of 4 succeed. The threshold is met, so the
        // batch succeeds WITH failures in it — that is the whole point of quorum.
        var instanceId = await RunCaseAsync("run-join-quorum", "DOC-1", "DOC-2", "DOC-FAIL-A", "DOC-FAIL-B");
        await WaitForSettledAsync(instanceId);

        AssertSummary(await GetAttributesAsync(Workflow, instanceId), total: 4, succeeded: 2, failed: 2);
    }

    [Fact]
    public async Task JoinQuorum_MinSuccessNotMet_FailsTheJoin()
    {
        // One short of minSuccess = 2. The only difference from the test above is the item mix, so
        // a pair that both passed would prove the threshold is never actually compared.
        var instanceId = await RunCaseAsync("run-join-quorum", "DOC-1", "DOC-FAIL-A", "DOC-FAIL-B");
        await WaitForFailedAsync(instanceId, "quorum minSuccess=2 was met by only 1 success");

        var summary = Summary(await GetAttributesAsync(Workflow, instanceId));
        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        Assert.True(summary.GetProperty("succeeded").GetInt32() < 2,
            "the case is only meaningful while fewer than minSuccess items succeed");
    }

    [Fact]
    public async Task JoinQuorum_EmptyCollection_FailsBecauseZeroCannotMeetAThreshold()
    {
        // The asymmetry with 'all' above is the interesting part: quorum has no empty-batch special
        // case, it just cannot clear a threshold of >= 1 with 0 successes.
        var instanceId = await RunCaseAsync("run-join-quorum");
        await WaitForFailedAsync(instanceId, "an empty batch cannot meet quorum minSuccess=2");
    }

    // ── join.policy: firstSuccess ────────────────────────────────────────────

    [Fact]
    public async Task JoinFirstSuccess_OneItemSucceeds_SettlesDespiteTheFailures()
    {
        var instanceId = await RunCaseAsync("run-join-first-success", "DOC-FAIL-A", "DOC-1", "DOC-FAIL-B");
        await WaitForSettledAsync(instanceId);

        var summary = Summary(await GetAttributesAsync(Workflow, instanceId));
        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        Assert.True(summary.GetProperty("succeeded").GetInt32() >= 1,
            "firstSuccess succeeded, so at least one item must be reported as successful");
    }

    [Fact]
    public async Task JoinFirstSuccess_NoItemSucceeds_FailsTheJoin()
    {
        var instanceId = await RunCaseAsync("run-join-first-success", "DOC-FAIL-A", "DOC-FAIL-B");
        await WaitForFailedAsync(instanceId, "firstSuccess needs one success and got none");
    }

    [Fact]
    public async Task JoinFirstSuccess_EmptyCollection_FailsLikeQuorumWithMinSuccessOne()
    {
        // firstSuccess IS quorum(minSuccess = 1). The two must never disagree on the same input,
        // and the empty batch is the input where a special case would make them diverge.
        var instanceId = await RunCaseAsync("run-join-first-success");
        await WaitForFailedAsync(instanceId, "an empty batch cannot produce the one success firstSuccess needs");
    }

    // ── early stop ───────────────────────────────────────────────────────────

    /// <summary>
    /// KNOWN RED — finding F1. <c>firstSuccess</c> cancels the remaining items once one succeeds,
    /// and <c>FanOutErrorCodes.ItemCancelled</c> plus the developer guide both promise
    /// <c>FanOut:ItemCancelled</c> on those items. Measured: items already in flight get
    /// <c>Task:Unknown:process-document-task:TaskCanceledException</c> — the inner task's raw
    /// exception name, with the task key embedded — while an item cancelled before it started gets
    /// the documented code. Both codes appear in ONE batch, so an author branching on the
    /// documented string silently misses most cancelled items.
    /// </summary>
    [Fact]
    public async Task EarlyStop_CancelledItems_CarryTheDocumentedFanOutItemCancelledCode()
    {
        var instanceId = await RunCaseAsync(
            "run-join-first-success", "DOC-1", "DOC-2", "DOC-3", "DOC-4", "DOC-5");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        var cancelled = Rows(attributes).Where(row => !row.GetProperty("isSuccess").GetBoolean()).ToArray();

        Assert.True(cancelled.Length >= 1,
            "firstSuccess over five succeedable items must early-stop at least one sibling, " +
            "otherwise this test cannot say anything about the cancellation code");

        var wrong = cancelled.Where(row => Text(row, "errorCode") != ItemCancelledCode).ToArray();

        Assert.True(wrong.Length == 0,
            $"{wrong.Length} of {cancelled.Length} early-stop-cancelled items did not carry " +
            $"'{ItemCancelledCode}'. FanOutErrorCodes and docs/domain/fan-out-task.md both name it " +
            "as the code for an item cancelled by the join policy's early stop, and workflow " +
            "authors branch on that string. Codes seen: " +
            string.Join(", ", cancelled.Select(row => $"{Key(row)}={Text(row, "errorCode")}")));
    }

    // ── timeouts: itemTimeoutSeconds vs batchTimeoutSeconds ──────────────────

    [Fact]
    public async Task ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut()
    {
        // itemTimeoutSeconds 1, batchTimeoutSeconds 30: the straggler blows its OWN deadline while
        // the batch has 29s to spare. The two codes must not be conflated, and summary.timedOut is
        // the BATCH's flag — an item timeout must not raise it.
        await AssertStragglerRouteIsActuallySlowAsync();

        var instanceId = await RunCaseAsync("run-item-timeout", "DOC-1", "DOC-SLOW-A", "DOC-3");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 3, succeeded: 2, failed: 1);

        var straggler = Row(attributes, "DOC-SLOW-A");
        Assert.False(straggler.GetProperty("isSuccess").GetBoolean());

        // Asserted BEFORE the error code on purpose. Everything above and this line is CORRECT
        // today — the deadline fires, the right item fails, the siblings survive, and the batch flag
        // stays down. Only the code attribution below is broken (F1), so ordering the sound claims
        // first keeps them genuinely verified on every run instead of being skipped by the failure.
        Assert.False(Summary(attributes).GetProperty("timedOut").GetBoolean(),
            "summary.timedOut reports the BATCH deadline. An item timeout with 29s of batch budget " +
            "left must not raise it, or a flow cannot tell a slow item from a blown batch.");

        // KNOWN RED — finding F1. Measured: Task:Unknown:process-document-task:TaskCanceledException.
        // The item DID time out; only its attribution is wrong, because the inner task's
        // TaskCanceledException is normalized before the fan-out layer can name the cause.
        Assert.Equal(ItemTimeoutCode, Text(straggler, "errorCode"));
    }

    [Fact]
    public async Task BatchTimeout_SerialBatch_StampsBatchTimeoutAndMarksTheBatchTimedOut()
    {
        // maxDegreeOfParallelism 1 with three ~1500ms items cannot finish inside a 2s batch
        // deadline. allSettled means the TASK still succeeds — a timed-out batch is data.
        await AssertStragglerRouteIsActuallySlowAsync();

        var instanceId = await RunCaseAsync("run-batch-timeout-serial", "DOC-SLOW-A", "DOC-SLOW-B", "DOC-SLOW-C");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        var summary = Summary(attributes);

        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        Assert.True(summary.GetProperty("timedOut").GetBoolean(),
            "the batch deadline fired with items outstanding, so summary.timedOut must be true");

        // Bounds, not exact counts: which item is in flight when a 2s deadline fires is not
        // something an outcome assertion should pretend to know.
        Assert.True(summary.GetProperty("failed").GetInt32() >= 1,
            "a serial batch of three ~1500ms items cannot settle all of them inside 2s");

        var batchTimedOut = Rows(attributes).Where(row => Text(row, "errorCode") == BatchTimeoutCode).ToArray();
        Assert.True(batchTimedOut.Length >= 1,
            $"no item carried '{BatchTimeoutCode}'; codes seen: " +
            string.Join(", ", Rows(attributes).Select(row => $"{Key(row)}={Text(row, "errorCode")}")));
    }

    [Fact]
    public async Task MaxDegreeOfParallelism_RaisedCeiling_LetsEveryItemFinishInsideTheSameBudget()
    {
        // The control arm for the test above. IDENTICAL config and IDENTICAL items — only
        // maxDegreeOfParallelism differs (4 instead of 1). Three ~1500ms items run concurrently and
        // all settle inside the same 2s batch deadline the serial arm blew. The observable is how
        // many items SUCCEEDED and the only variable is the ceiling, which is what makes this a
        // concurrency assertion rather than a timing one.
        //
        // The guard is load-bearing HERE above all: without a real delay every item finishes in
        // milliseconds at any ceiling, and this arm passes while proving nothing.
        await AssertStragglerRouteIsActuallySlowAsync();

        var instanceId = await RunCaseAsync("run-parallel-baseline", "DOC-SLOW-A", "DOC-SLOW-B", "DOC-SLOW-C");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 3, succeeded: 3, failed: 0);
        Assert.False(Summary(attributes).GetProperty("timedOut").GetBoolean(),
            "with the ceiling raised the same three items fit inside the same 2s deadline");
    }

    // ── per-item errorBoundary ───────────────────────────────────────────────

    /// <summary>
    /// Characterization of a per-item <c>ignore</c> rule — see finding F3. MEASURED behaviour: a
    /// wildcard <c>ignore</c> does NOT convert a failed item into a successful one. The item stays
    /// <c>isSuccess: false</c>, still counts toward <c>failed</c>, and therefore still fails a
    /// <c>join: all</c> batch exactly as it would with no boundary at all.
    /// <para>
    /// This test pins that observation rather than the intent, because the intent is undocumented:
    /// the developer guide spells out only the <c>retry</c> case ("a retry-exhausted item becomes
    /// one Failed entry"), and <c>ErrorAction.Ignore</c> maps to <c>ShouldContinue</c>, which is
    /// about not propagating the error rather than about fabricating success. If the intended
    /// semantics turn out to be "an ignored item counts as successful", THIS is the test to invert —
    /// do not delete it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PerItemErrorBoundary_Ignore_DoesNotConvertAFailedItemIntoASuccess()
    {
        var instanceId = await RunCaseAsync("run-item-boundary-ignore", "DOC-1", "DOC-FAIL-A", "DOC-3");
        await WaitForFailedAsync(instanceId,
            "a wildcard per-item 'ignore' does not rescue the item, so join 'all' still fails");

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(3, Summary(attributes).GetProperty("total").GetInt32());
        Assert.False(Row(attributes, "DOC-FAIL-A").GetProperty("isSuccess").GetBoolean(),
            "measured contract: 'ignore' leaves the item failed. If this now passes as a success, " +
            "the semantics changed — see finding F3 and invert this test deliberately.");
    }

    [Fact]
    public async Task PerItemErrorBoundary_Retry_ContainsExhaustionToItsOwnItem()
    {
        // A permanently failing item with a retry rule (maxRetries 2) exhausts its retries and
        // becomes ONE failed entry. The claim under test is containment: the retried item must not
        // take its siblings, or the batch, with it.
        //
        // Retry ATTEMPT counts are deliberately not asserted — they live only in the InstanceTask
        // journal row {fanOutTaskKey}#{index}, reachable only via the monitoring host (4203) that
        // no test stack starts. See the findings doc.
        var instanceId = await RunCaseAsync("run-item-boundary-retry", "DOC-1", "DOC-FAIL-A", "DOC-3");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 3, succeeded: 2, failed: 1);

        Assert.False(Row(attributes, "DOC-FAIL-A").GetProperty("isSuccess").GetBoolean());
        Assert.True(Row(attributes, "DOC-1").GetProperty("isSuccess").GetBoolean(),
            "a sibling of the retried item must be unaffected by its retry loop");
        Assert.True(Row(attributes, "DOC-3").GetProperty("isSuccess").GetBoolean(),
            "a sibling of the retried item must be unaffected by its retry loop");
    }

    // ── mode ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// KNOWN RED — finding F2. <c>mode: "durable"</c> IS refused at publish (so the reserved mode
    /// never becomes a live definition — good), but it is refused with an opaque HTTP 500
    /// "An internal error occurred during your request!" rather than the 400 validation problem
    /// every other bad component gets. <c>FanOutTask.Configure</c>'s <c>ArgumentException</c> is
    /// evidently not mapped into the component-validation path, so the author is told nothing about
    /// what is wrong with their component.
    /// </summary>
    [Fact]
    public async Task DurableMode_IsRefusedWithAnActionableValidationError()
    {
        // Version is unique per run so a 409 "already exists" can never be mistaken for the
        // rejection under test. The component is posted from the test rather than kept on disk: an
        // intentionally invalid file under core/Tasks/ would be published by the SDK's
        // LocalDomainPublisher on every fixture start and its FAIL line would read like a real
        // regression forever after.
        var uniqueVersion = $"9.0.{(int)DateTime.UtcNow.TimeOfDay.TotalSeconds}";
        var component = new
        {
            key = "fanout-case-durable-mode-probe-task",
            version = uniqueVersion,
            domain = "core",
            flow = "sys-tasks",
            flowVersion = "1.0.0",
            tags = new[] { "integration-test", "fan-out-config-matrix", "durable-mode-probe" },
            attributes = new
            {
                type = "21",
                config = new
                {
                    mode = "durable",
                    itemsPath = "$.documents",
                    itemAlias = "document",
                    task = new { key = "process-document-task", domain = "core", flow = "sys-tasks", version = "1.0.0" },
                    execution = new { maxDegreeOfParallelism = 4, itemTimeoutSeconds = 10, batchTimeoutSeconds = 60 },
                    join = new { policy = "allSettled", resultKey = ResultKey, ordered = true }
                }
            }
        };

        var (status, body) = await SendRawAsync(
            HttpMethod.Post, "api/v1/definitions/publish", component, Headers());

        // Part 1 — it must be refused. This part PASSES: durable never becomes a live definition.
        Assert.True((int)status >= 400,
            $"publish ACCEPTED mode 'durable' with {(int)status}. The mode is reserved and " +
            "FanOutTask.Configure throws on it, so accepting the definition would defer the failure " +
            $"to the first execution of whatever flow references it. Response: {Trim(body)}");

        // Part 2 — the refusal must tell the author what is wrong. This part is the filed defect:
        // measured 500 + "An internal error occurred during your request!".
        Assert.True((int)status < 500,
            $"publish refused mode 'durable' with {(int)status} — an unhandled-exception shape, not " +
            "a validation error. A bad workflow gets 400 App:900006 naming the exact field; a task " +
            "whose config throws in Configure gets an opaque 500 with nothing actionable in it. " +
            $"See finding F2. Response: {Trim(body)}");

        Assert.True(
            body.Contains("durable", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("inline", StringComparison.OrdinalIgnoreCase),
            $"the refusal body named neither the offending mode nor the supported one. Response: {Trim(body)}");
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a matrix instance carrying <paramref name="documentIds"/> as its <c>documents</c>
    /// array and fires the case transition, whose target state's onEntry IS the batch under test.
    /// Pass no ids for the empty-collection cases.
    /// <para>
    /// The transition is only required to be ACCEPTED, not to succeed: half the matrix expects the
    /// batch to fail. The verdict is asserted by the caller via <see cref="WaitForSettledAsync"/> or
    /// <see cref="WaitForFailedAsync"/>.
    /// </para>
    /// </summary>
    private async Task<string> RunCaseAsync(string caseTransition, params string[] documentIds)
    {
        var instanceId = await StartAsync(Workflow, new
        {
            testId = $"{caseTransition}-{Guid.NewGuid():N}",
            documents = documentIds
                .Select(id => new { id, url = $"https://example.invalid/{id}.pdf" })
                .ToArray()
        });

        // The start transition runs its own pipeline; firing the case transition while the instance
        // is still Busy is refused with 409. Wait for it to settle first.
        await WaitUntilSettledAsync(Workflow, instanceId);
        await AssertNotFaultedAsync(Workflow, instanceId);

        var url = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}" +
                  $"/transitions/{caseTransition}?sync=false";
        var (status, body) = await SendRawAsync(HttpMethod.Patch, url, new { }, Headers());

        Assert.True((int)status < 400,
            $"'{caseTransition}' was refused with {(int)status}: {Trim(body)}");

        return instanceId;
    }

    /// <summary>The join succeeded: the case state's auto transition fired.</summary>
    private Task WaitForSettledAsync(string instanceId) =>
        WaitForTerminalAsync(instanceId, SettledState, FailedState,
            "the join was expected to SUCCEED but the boundary routed it to case-failed");

    /// <summary>The join failed: the global rollback boundary routed to <c>case-failed</c>.</summary>
    private Task WaitForFailedAsync(string instanceId, string because) =>
        WaitForTerminalAsync(instanceId, FailedState, SettledState,
            $"the join SUCCEEDED and the instance settled, but it should have failed: {because}");

    /// <summary>
    /// Waits for one of the two terminal states, failing immediately and by name if the OTHER one
    /// is reached. Both outcomes are terminal, so waiting out the budget on the wrong one would
    /// report a timeout and hide the actual verdict.
    /// </summary>
    private Task WaitForTerminalAsync(string instanceId, string expected, string opposite, string oppositeMessage) =>
        WaitUntilAsync(
            async () =>
            {
                var (state, _) = await GetInstanceStateAsync(Workflow, instanceId);
                if (state == expected) return true;

                Assert.True(state != opposite,
                    $"{oppositeMessage}. {await DescribeAsync(Workflow, instanceId)}");

                return false;
            },
            $"{Workflow}/{instanceId} never reached '{expected}'",
            CaseBudget);

    /// <summary>
    /// Precondition guard for the three cases that need a genuinely slow inner call.
    /// <para>
    /// This is the one place a stopwatch appears, and it measures the MOCK, not the runtime — it
    /// asserts the fixture's premise holds, so that a broken mock cannot masquerade as either a
    /// runtime bug (the timeout arms) or a passing concurrency proof (the control arm). If MockLab
    /// is unreachable the guard stays silent: the containerised path resolves it on a different
    /// host and there is nothing to check from here.
    /// </para>
    /// </summary>
    private static async Task AssertStragglerRouteIsActuallySlowAsync()
    {
        string body;
        Stopwatch clock;

        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            clock = Stopwatch.StartNew();
            try
            {
                using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(StragglerUrl, content);
                clock.Stop();
                if (!response.IsSuccessStatusCode) return;
                body = await response.Content.ReadAsStringAsync();
            }
            catch (Exception)
            {
                return; // not reachable from here — nothing to assert
            }
        }

        // Check 1 — is the STRAGGLER mock the one answering? This is the non-timing check, and it is
        // the one that actually caught the original bug: MockLab matches routes by PREFIX, so while
        // the slow mock was authored at "documents/process-slow" every request to it was answered by
        // the "documents/process" mock registered before it. Proof at the time: a nonsense path
        // "documents/process-XYZQQ" also returned the fast mock's body. The slow mock was
        // unreachable, so its delayMs never applied and the "straggler" came back in 13-46ms.
        Assert.Contains(StragglerBodyMarker, body);

        // Check 2 — is the delay actually applied? The floor is the largest itemTimeoutSeconds any
        // straggler case configures, not merely "more than zero": a delay under that floor cannot
        // make an item miss its deadline, so the case would pass while proving nothing.
        Assert.True(clock.Elapsed >= StragglerFloor,
            $"ENVIRONMENT: MockLab's straggler route answered in {clock.ElapsedMilliseconds}ms; its seed " +
            $"configures delayMs={ConfiguredStragglerDelay.TotalMilliseconds:0} and this case needs more " +
            $"than {StragglerFloor.TotalMilliseconds:0}ms to mean anything. itemTimeoutSeconds is a whole " +
            "number of seconds >= 1, so without a real delay no item can exceed its deadline. This is " +
            "not a FanOutTask defect. Recreate the container so it re-seeds — a plain `docker restart` " +
            "does NOT (MockLab persists mocks in a container-local DB and skips collections whose name " +
            "already exists): docker compose up -d --force-recreate mocklab");
    }

    // ── readers ──────────────────────────────────────────────────────────────

    private static JsonElement Summary(JsonElement attributes)
    {
        Assert.True(attributes.TryGetProperty(SummaryKey, out var summary),
            $"instance data carried no '{SummaryKey}' — the runtime's default output packaging " +
            "never landed, so every count below would be vacuous");
        return summary;
    }

    private static JsonElement[] Rows(JsonElement attributes)
    {
        Assert.True(attributes.TryGetProperty(ResultKey, out var results),
            $"instance data carried no '{ResultKey}' — the join's resultKey never landed");
        return results.EnumerateArray().ToArray();
    }

    private static JsonElement Row(JsonElement attributes, string documentId)
    {
        var rows = Rows(attributes);
        var match = rows.Where(row => Key(row) == documentId).ToArray();

        Assert.True(match.Length == 1,
            $"expected exactly one result row for '{documentId}', found {match.Length}; " +
            $"rows: {string.Join(", ", rows.Select(Key))}");

        return match[0];
    }

    private static void AssertSummary(JsonElement attributes, int total, int succeeded, int failed)
    {
        var summary = Summary(attributes);
        Assert.Equal(total, summary.GetProperty("total").GetInt32());
        Assert.Equal(succeeded, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(failed, summary.GetProperty("failed").GetInt32());
    }

    /// <summary>
    /// A failed join must still have written its result set. <c>FanOutTaskExecutor</c> returns
    /// <c>Result.Ok(new TaskInvocationResult { IsSuccess = false, Data = … })</c> rather than
    /// <c>TaskInvocationResult.Failure(…)</c> specifically so the data survives; a regression to the
    /// Failure factory would leave a boundary or auto-transition with nothing to branch on.
    /// </summary>
    private static void AssertFailedJoinStillCarriedItsData(JsonElement attributes)
    {
        var summary = Summary(attributes);
        Assert.True(summary.GetProperty("total").GetInt32() > 0,
            "a failed join over a non-empty batch must still report its item counts");
        Assert.NotEmpty(Rows(attributes));
    }

    private static string Key(JsonElement row) => row.GetProperty("itemKey").GetString() ?? "";

    private static string Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Trim(string body) => body.Length > 600 ? body[..600] + "…" : body;
}
