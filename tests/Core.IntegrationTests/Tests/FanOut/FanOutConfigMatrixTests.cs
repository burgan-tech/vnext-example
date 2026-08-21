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
/// <b>What this asserts.</b> All four <c>join.policy</c> values on both sides of their verdict;
/// <c>join.minSuccess</c> met and not met; the empty-collection rule (<c>all</c> / <c>allSettled</c>
/// succeed vacuously, <c>quorum</c> / <c>firstSuccess</c> fail because a threshold of at least one
/// cannot be met by zero items); <c>itemTimeoutSeconds</c> and <c>batchTimeoutSeconds</c> producing
/// DISTINCT error codes and disagreeing about <c>summary.timedOut</c>;
/// <c>maxDegreeOfParallelism</c> actually bounding concurrency; a per-item <c>errorBoundary</c>
/// applying per item without taking the batch down; and <c>mode: "durable"</c> being refused.
/// </para>
/// <para>
/// <b>How a join verdict is observed.</b> The flow puts exactly one thing in each case state's
/// onEntry — that case's fan-out batch — and hangs ONE unconditional auto transition off the
/// state. So the join's verdict is the instance's fate, with nothing else able to produce it:
/// </para>
/// <list type="bullet">
///   <item>join succeeded ⇒ the onEntry task succeeded ⇒ the auto transition fires ⇒
///   <c>case-settled</c>.</item>
///   <item>join failed ⇒ the onEntry task failed ⇒ the workflow declares NO error boundary at any
///   level ⇒ the instance Faults in the case state.</item>
/// </list>
/// <para>
/// Do not "fix" the workflow by adding an error boundary to stop the faulting. Every failed-join
/// case here would turn into a silent success and this class would stop testing anything.
/// </para>
/// <para>
/// <b>Every shape asserted here is produced by the RUNTIME.</b> <c>FanOutCaseMapping</c> overrides
/// <c>ItemInputHandler</c> only (an <c>HttpTask</c>'s per-item URL lives on the cloned task and no
/// other hook can reach it) and deliberately leaves <c>OutputHandler</c> unoverridden, so
/// <c>caseResults</c> and <c>caseResultsSummary</c> are the executor's own
/// <c>BuildDefaultOutput</c> packaging.
/// </para>
/// <para>
/// <b>No wall-clock assertions.</b> The concurrency and timeout cases assert on error codes and
/// counts only. The two timeout arms do rely on MockLab's 1500ms straggler route being slower than
/// a 2s batch deadline; that margin is the one environmental sensitivity in this class and is
/// documented in the scenario README. If the parallel control arm ever flakes, raise
/// <c>batchTimeoutSeconds</c> on BOTH arms together — they are a matched pair and differ only in
/// <c>maxDegreeOfParallelism</c>.
/// </para>
/// <para>
/// <b>What is deliberately NOT asserted.</b> Retry ATTEMPT counts. A per-item retry's attempts are
/// visible only in the <c>InstanceTask</c> journal row keyed <c>{fanOutTaskKey}#{index}</c>, which
/// is reachable only through the monitoring host (port 4203) that neither the SDK's container stack
/// nor the local dev stack starts, and MockLab's sequential-response feature is per-mock rather
/// than per-item so it cannot express "fail once then succeed" under concurrency. The retry case
/// therefore asserts the claim that IS observable and load-bearing — retry exhaustion stays
/// contained to its own item — rather than faking the attempt count.
/// </para>
/// </summary>
public class FanOutConfigMatrixTests : WorkflowTestBase
{
    private const string Workflow = "fan-out-config-matrix";
    private const string SettledState = "case-settled";
    private const string ResultKey = "caseResults";
    private const string SummaryKey = "caseResultsSummary";

    // FanOutErrorCodes — the task's PUBLIC contract (BBT.Workflow.Tasks.Executors.FanOutErrorCodes).
    // Workflow authors branch on these strings, so they are asserted literally.
    private const string ItemTimeoutCode = "FanOut:ItemTimeout";
    private const string BatchTimeoutCode = "FanOut:BatchTimeout";

    /// <summary>
    /// Generous enough for the serial arm (three 1500ms items behind a concurrency limit of 1) plus
    /// the async accept, the auto transition and polling slack.
    /// </summary>
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
    public async Task JoinAll_OneItemFails_FailsTheJoinAndFaultsTheInstance()
    {
        // 'all' is the atomic policy: one failure is the batch's failure. Contrast with
        // FanOutDocumentsTests, where the same item mix under 'allSettled' branches instead.
        var instanceId = await RunCaseAsync("run-join-all", "DOC-1", "DOC-FAIL-A", "DOC-3");
        await WaitForFaultAsync(instanceId, "join policy 'all' must fail the batch on a failed item");
    }

    [Fact]
    public async Task JoinAll_EmptyCollection_SucceedsVacuously()
    {
        // Vacuous truth: with no items there is no item that failed. This is a real authoring
        // hazard — a batch over an empty list silently "succeeds" — so it is pinned deliberately.
        var instanceId = await RunCaseAsync("run-join-all");
        await WaitForSettledAsync(instanceId);

        AssertSummary(await GetAttributesAsync(Workflow, instanceId), total: 0, succeeded: 0, failed: 0);
    }

    // ── join.policy: allSettled ──────────────────────────────────────────────

    [Fact]
    public async Task JoinAllSettled_EmptyCollection_Succeeds()
    {
        // allSettled always succeeds; an empty batch is not a special case for it.
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
    public async Task JoinQuorum_MinSuccessNotMet_FailsTheJoinAndFaultsTheInstance()
    {
        // One short of minSuccess = 2. The only difference from the test above is the item mix, so
        // a pair that both passed would prove the threshold is never actually compared.
        var instanceId = await RunCaseAsync("run-join-quorum", "DOC-1", "DOC-FAIL-A", "DOC-FAIL-B");
        await WaitForFaultAsync(instanceId, "quorum minSuccess=2 was met by only 1 success");
    }

    [Fact]
    public async Task JoinQuorum_EmptyCollection_FailsBecauseZeroCannotMeetAThreshold()
    {
        // The asymmetry with 'all' above is the interesting part: quorum has no empty-batch special
        // case, it just cannot clear a threshold of >= 1 with 0 successes.
        var instanceId = await RunCaseAsync("run-join-quorum");
        await WaitForFaultAsync(instanceId, "an empty batch cannot meet quorum minSuccess=2");
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
    public async Task JoinFirstSuccess_NoItemSucceeds_FailsTheJoinAndFaultsTheInstance()
    {
        var instanceId = await RunCaseAsync("run-join-first-success", "DOC-FAIL-A", "DOC-FAIL-B");
        await WaitForFaultAsync(instanceId, "firstSuccess needs one success and got none");
    }

    [Fact]
    public async Task JoinFirstSuccess_EmptyCollection_FailsLikeQuorumWithMinSuccessOne()
    {
        // firstSuccess IS quorum(minSuccess = 1). The two must never disagree on the same input,
        // and the empty batch is the input where a special case would make them diverge.
        var instanceId = await RunCaseAsync("run-join-first-success");
        await WaitForFaultAsync(instanceId, "an empty batch cannot produce the one success firstSuccess needs");
    }

    // ── timeouts: itemTimeoutSeconds vs batchTimeoutSeconds ──────────────────

    [Fact]
    public async Task ItemTimeout_StampsItemTimeoutOnTheStraggler_AndLeavesTheBatchNotTimedOut()
    {
        // itemTimeoutSeconds 1, batchTimeoutSeconds 30: the 1500ms straggler blows its OWN deadline
        // while the batch has 30s to spare. The two codes must not be conflated, and
        // summary.timedOut is the batch's flag — an item timeout must NOT set it.
        var instanceId = await RunCaseAsync("run-item-timeout", "DOC-1", "DOC-SLOW-A", "DOC-3");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 3, succeeded: 2, failed: 1);

        var straggler = Row(attributes, "DOC-SLOW-A");
        Assert.False(straggler.GetProperty("isSuccess").GetBoolean(), "the 1500ms item must not succeed under a 1s item deadline");
        Assert.Equal(ItemTimeoutCode, Text(straggler, "errorCode"));

        Assert.False(Summary(attributes).GetProperty("timedOut").GetBoolean(),
            "summary.timedOut reports the BATCH deadline. An item timeout with 29s of batch budget " +
            "left must not raise it, or a flow cannot tell a slow item from a blown batch.");
    }

    [Fact]
    public async Task BatchTimeout_SerialBatch_StampsBatchTimeoutAndMarksTheBatchTimedOut()
    {
        // maxDegreeOfParallelism 1 with three 1500ms items cannot finish inside a 2s batch
        // deadline, so the deadline is reached with items still outstanding. allSettled means the
        // TASK still succeeds — the batch timing out is data, not an error.
        var instanceId = await RunCaseAsync("run-batch-timeout-serial", "DOC-SLOW-A", "DOC-SLOW-B", "DOC-SLOW-C");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        var summary = Summary(attributes);

        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        Assert.True(summary.GetProperty("timedOut").GetBoolean(),
            "the batch deadline fired with items outstanding, so summary.timedOut must be true");

        // Counts are asserted as bounds rather than exact numbers on purpose: exactly which item is
        // in flight when a 2s deadline fires is not something an outcome assertion should pretend
        // to know. The load-bearing claims are that the concurrency limit prevented some items from
        // finishing, and that the cut items are attributed to the BATCH deadline, not their own.
        Assert.True(summary.GetProperty("failed").GetInt32() >= 1,
            "a serial batch of three 1500ms items cannot settle all of them inside 2s");

        var batchTimedOut = Rows(attributes).Where(row => Text(row, "errorCode") == BatchTimeoutCode).ToArray();
        Assert.True(batchTimedOut.Length >= 1,
            $"no item carried '{BatchTimeoutCode}'; codes seen: " +
            string.Join(", ", Rows(attributes).Select(row => $"{Key(row)}={Text(row, "errorCode")}")));
    }

    [Fact]
    public async Task MaxDegreeOfParallelism_RaisedCeiling_LetsEveryItemFinishInsideTheSameBudget()
    {
        // The control arm for the test above. IDENTICAL config and IDENTICAL items — only
        // maxDegreeOfParallelism differs (4 instead of 1). Three 1500ms items run concurrently and
        // all settle inside the same 2s batch deadline that the serial arm blew.
        //
        // This is what makes the pair a concurrency assertion rather than a timing one: the
        // observable is how many items SUCCEEDED, and the only variable is the ceiling.
        var instanceId = await RunCaseAsync("run-parallel-baseline", "DOC-SLOW-A", "DOC-SLOW-B", "DOC-SLOW-C");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 3, succeeded: 3, failed: 0);
        Assert.False(Summary(attributes).GetProperty("timedOut").GetBoolean(),
            "with the ceiling raised the same three items fit inside the same 2s deadline");
    }

    // ── per-item errorBoundary ───────────────────────────────────────────────

    [Fact]
    public async Task PerItemErrorBoundary_Ignore_KeepsAFailingItemFromTakingTheBatchDown()
    {
        // The strongest available proof that the per-item boundary is applied PER ITEM: this case
        // uses join.policy 'all', under which JoinAll_OneItemFails (same item mix) faults. The only
        // difference is the component's errorBoundary — a wildcard 'ignore'. If the boundary is
        // honoured for each item, the failing item is not a failure, so 'all' is satisfied and the
        // instance settles. If it is ignored, this test faults exactly like the 'all' test does.
        var instanceId = await RunCaseAsync("run-item-boundary-ignore", "DOC-1", "DOC-FAIL-A", "DOC-3");
        await WaitForSettledAsync(instanceId);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.Equal(3, Summary(attributes).GetProperty("total").GetInt32());
        Assert.Equal(0, Summary(attributes).GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task PerItemErrorBoundary_Retry_ContainsExhaustionToItsOwnItem()
    {
        // A permanently failing item with a retry rule (maxRetries 2) exhausts its retries and
        // becomes ONE failed entry. The claim under test is containment: the retried item must not
        // take its siblings, or the batch, with it.
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

    [Fact]
    public async Task DurableMode_IsRefusedRatherThanSilentlyAccepted()
    {
        // 'durable' is RESERVED: FanOutTask.Configure throws
        //   "FanOutTask mode 'durable' is not supported yet. Only 'inline' is available"
        // so a component declaring it must never become a live definition. The component is posted
        // from the test rather than kept on disk on purpose — an intentionally invalid file under
        // core/Tasks/ would be published by the SDK's LocalDomainPublisher on every fixture start
        // and its FAIL line would read like a real regression forever after.
        //
        // The version is unique per run so a 409 "already exists" can never be mistaken for the
        // rejection under test.
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
                    task = new
                    {
                        key = "process-document-task",
                        domain = "core",
                        flow = "sys-tasks",
                        version = "1.0.0"
                    },
                    execution = new
                    {
                        maxDegreeOfParallelism = 4,
                        itemTimeoutSeconds = 10,
                        batchTimeoutSeconds = 60
                    },
                    join = new
                    {
                        policy = "allSettled",
                        resultKey = ResultKey,
                        ordered = true
                    }
                }
            }
        };

        var (status, body) = await SendRawAsync(
            HttpMethod.Post, "api/v1/definitions/publish", component, Headers());

        Assert.True((int)status >= 400,
            $"publish ACCEPTED mode 'durable' with {(int)status}. The mode is reserved and " +
            "FanOutTask.Configure throws on it, so accepting the definition defers the failure to " +
            $"the first execution of whatever flow references it. Response: {Trim(body)}");

        Assert.True(
            body.Contains("durable", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("inline", StringComparison.OrdinalIgnoreCase),
            $"publish refused the component with {(int)status} but for an unrecognisable reason — " +
            "this test must fail on the durable-mode rejection, not on some unrelated validation " +
            $"error that would mask it. Response: {Trim(body)}");
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a matrix instance carrying <paramref name="documentIds"/> as its <c>documents</c>
    /// array and fires the case transition, whose target state's onEntry IS the fan-out batch under
    /// test. Pass no ids for the empty-collection cases.
    /// <para>
    /// The transition is only required to be ACCEPTED here, not to succeed: half the matrix expects
    /// the batch to fail, and a failing onEntry task is a failing transition. Settling is therefore
    /// awaited by the caller via <see cref="WaitForSettledAsync"/> or
    /// <see cref="WaitForFaultAsync"/>, which is where the actual verdict is asserted.
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
        WaitForInstanceStateAsync(Workflow, instanceId, SettledState, timeout: CaseBudget);

    /// <summary>
    /// The join FAILED: no error boundary exists anywhere in this workflow, so the instance faults.
    /// <para>
    /// <see cref="WorkflowTestBase.WaitForInstanceStateAsync"/> deliberately fails fast on a fault,
    /// which is right for every other suite and exactly wrong here — a fault is the expected
    /// outcome for six of these cases. Hence this local waiter. It also guards the opposite
    /// mistake: an instance that reached <c>case-settled</c> can never fault afterwards, so seeing
    /// the settled state means the join succeeded when it should not have, and burning the whole
    /// budget would report that as a timeout instead of naming it.
    /// </para>
    /// </summary>
    private Task WaitForFaultAsync(string instanceId, string because) =>
        WaitUntilAsync(
            async () =>
            {
                var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
                if (status == "F") return true;

                Assert.True(state != SettledState,
                    $"the join SUCCEEDED and the instance settled, but it should have failed: {because}. " +
                    await DescribeAsync(Workflow, instanceId));

                return false;
            },
            $"{Workflow}/{instanceId} never faulted — {because}",
            CaseBudget);

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

    private static string Key(JsonElement row) => row.GetProperty("itemKey").GetString() ?? "";

    private static string Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Trim(string body) => body.Length > 600 ? body[..600] + "…" : body;
}
