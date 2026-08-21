using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.FanOut;

/// <summary>
/// fan-out-documents: the flow built to exercise <c>FanOutTask</c> (TaskType 21, inline mode) —
/// a collection resolved from instance data at runtime, one referenced inner task run per item
/// in parallel, and the per-item outcomes joined by policy into ONE task result and ONE
/// instance-data write.
/// <para>
/// <b>What this asserts.</b> The happy path (5 documents, all succeed, full result set under the
/// join's <c>resultKey</c> plus a correct <c>{resultKey}Summary</c>), the partial-failure branch
/// (2 of 5 fail deterministically at MockLab, the summary reports <c>failed: 2</c>, the failed
/// rows carry error codes, and an auto transition routes to <c>documents-partial-failure</c>),
/// and — the one that matters most — the <b>single-write invariant</b>: the whole batch produces
/// exactly ONE new instance-data version, not N. That invariant is the entire reason the design
/// runs item handlers on discarded branch contexts with <c>SuppressDataApply</c> and funnels
/// everything through a single <c>OutputHandler</c>; if it regresses, fan-out becomes N
/// concurrent writers racing on one aggregate and every other guarantee here is worthless.
/// </para>
/// <para>
/// <b>How the invariant is measured, and why it is measured this way.</b> There is NO
/// orchestration-host endpoint that enumerates an instance's data versions — <c>GET
/// .../instances/{id}</c> returns only the latest merged <c>attributes</c>, the state function
/// carries no data version, and <c>GET .../instances/{id}/data</c> returns a single version with
/// no version string in the body. Version history lives only on the MONITORING host
/// (<c>/api/v1/monitor/.../data</c> → <c>versionHistory[]</c>, port 4203), which the testing SDK's
/// container stack does not start. So the flow reports its own version marks: the fan-out
/// mapping's <c>OutputHandler</c> stamps <c>versionSeenByFanOut</c> (read before its own write is
/// applied) and the very next onEntry task stamps <c>versionAfterFanOut</c>. Both reads are of
/// <c>Instance.LatestData.Version</c>, and nothing runs between them — no transition, no state
/// change, no other task. One patch of distance means one write. Five would mean five.
/// A second, independent probe corroborates it through the public data endpoint.
/// </para>
/// <para>
/// <b>Known coverage gap — the item journal.</b> Each item is journalled as its own
/// <c>InstanceTask</c> row keyed <c>{fanOutTaskKey}#{index}</c>
/// (<c>FanOutTaskExecutor</c> sets <c>JournalTaskKey = $"{task.Key}#{item.Index}"</c>). Those rows
/// are reachable only through the monitoring host's
/// <c>GET /api/v1/monitor/{domain}/workflows/{workflow}/instances/{id}/tasks</c>
/// (<c>taskDefinitionKey</c>), and the SDK neither starts that host nor exposes the endpoint.
/// The assertion is therefore ABSENT rather than faked. <c>api-tests/fan-out-documents/fanout-load.py
/// --monitor-url</c> checks it opt-in against a stack that does run monitoring; see this
/// directory's README before assuming it is covered here.
/// </para>
/// </summary>
public class FanOutDocumentsTests : WorkflowTestBase
{
    private const string Workflow = "fan-out-documents";
    private const string ProcessTransition = "process-documents";
    private const string CompletedState = "documents-completed";
    private const string PartialFailureState = "documents-partial-failure";

    private static readonly TimeSpan BatchBudget = TimeSpan.FromSeconds(90);

    public FanOutDocumentsTests(VNextTestEnvironment environment) : base(environment) { }

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FiveDocuments_AllSucceed_ReachCompletedWithTheFullResultSet()
    {
        var documents = new[] { "DOC-1", "DOC-2", "DOC-3", "DOC-4", "DOC-5" };
        var instanceId = await RunBatchAsync("fanout-happy", documents, CompletedState);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        var results = ReadResults(attributes);

        Assert.Equal(5, results.Length);
        Assert.All(results, row => Assert.True(
            row.GetProperty("isSuccess").GetBoolean(),
            $"item '{Key(row)}' failed: {Text(row, "errorCode")} {Text(row, "errorMessage")}"));

        // join.ordered is a no-op in inline mode precisely because results are ALWAYS sorted by
        // index — pin that, so a future durable mode cannot quietly change it here.
        Assert.Equal(documents, results.Select(Key).ToArray());
        Assert.Equal(Enumerable.Range(0, 5), results.Select(row => row.GetProperty("index").GetInt32()));

        AssertSummary(attributes, total: 5, succeeded: 5, failed: 0);
        Assert.False(Summary(attributes).GetProperty("timedOut").GetBoolean());
    }

    // ── the single-write invariant ───────────────────────────────────────────

    [Fact]
    public async Task TheWholeBatch_ProducesExactlyOneInstanceDataVersion()
    {
        var documents = new[] { "DOC-1", "DOC-2", "DOC-3", "DOC-4", "DOC-5" };
        var instanceId = await RunBatchAsync("fanout-single-write", documents, CompletedState);

        await AssertOneVersionForTheBatchAsync(instanceId, itemCount: documents.Length);
    }

    [Fact]
    public async Task TheWholeBatch_ProducesExactlyOneInstanceDataVersion_EvenWhenItemsFail()
    {
        // The failure path is the likelier regression: an implementation that "helpfully" persists
        // each failed item's error as it happens breaks the invariant without breaking the happy
        // path, and would pass every other test in this class.
        var documents = new[] { "DOC-1", "DOC-FAIL-A", "DOC-3", "DOC-FAIL-B", "DOC-5" };
        var instanceId = await RunBatchAsync("fanout-single-write-partial", documents, PartialFailureState);

        await AssertOneVersionForTheBatchAsync(instanceId, itemCount: documents.Length);
    }

    // ── partial failure ──────────────────────────────────────────────────────

    [Fact]
    public async Task TwoOfFiveFail_RoutesToPartialFailure_AndTheFailedRowsCarryErrorCodes()
    {
        var documents = new[] { "DOC-1", "DOC-FAIL-A", "DOC-3", "DOC-FAIL-B", "DOC-5" };
        var instanceId = await RunBatchAsync("fanout-partial", documents, PartialFailureState);

        var attributes = await GetAttributesAsync(Workflow, instanceId);

        // allSettled means the FanOut TASK succeeded — partial failure is data, not an error.
        // The instance must not be Faulted; it must have branched.
        await AssertNotFaultedAsync(Workflow, instanceId);
        AssertSummary(attributes, total: 5, succeeded: 3, failed: 2);

        var results = ReadResults(attributes);
        var failed = results.Where(row => !row.GetProperty("isSuccess").GetBoolean()).ToArray();
        var succeeded = results.Where(row => row.GetProperty("isSuccess").GetBoolean()).ToArray();

        Assert.Equal(new[] { "DOC-FAIL-A", "DOC-FAIL-B" }, failed.Select(Key).OrderBy(k => k).ToArray());
        Assert.Equal(new[] { "DOC-1", "DOC-3", "DOC-5" }, succeeded.Select(Key).OrderBy(k => k).ToArray());

        // The code itself is not pinned to a literal: FanOut stamps its own codes
        // (FanOut:ItemTimeout / BatchTimeout / ItemCancelled / ItemNotStarted / ItemFailed) but an
        // inner task's own error code passes through UNCHANGED, so a MockLab 500 legitimately
        // surfaces either. What must hold is that a failed row is never silently code-less.
        Assert.All(failed, row => Assert.False(
            string.IsNullOrWhiteSpace(Text(row, "errorCode")),
            $"failed item '{Key(row)}' carried no error code — a caller branching on which items " +
            "failed has nothing to branch on"));

        var failedIds = attributes.GetProperty("failedDocumentIds")
            .EnumerateArray().Select(id => id.GetString()).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { "DOC-FAIL-A", "DOC-FAIL-B" }, failedIds);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts an instance carrying <paramref name="documentIds"/> as its <c>documents</c> array,
    /// fires the transition whose target state's onEntry runs the fan-out batch, and waits for the
    /// auto transition to land the instance on <paramref name="expectedState"/>.
    /// </summary>
    private async Task<string> RunBatchAsync(string testId, string[] documentIds, string expectedState)
    {
        var instanceId = await StartAsync(Workflow, new
        {
            testId,
            documents = documentIds.Select(id => new { id, url = $"https://example.invalid/{id}.pdf" }).ToArray()
        });

        await RunAcceptedAsync(Workflow, instanceId, ProcessTransition, settleTimeout: BatchBudget);
        await WaitForInstanceStateAsync(Workflow, instanceId, expectedState, timeout: BatchBudget);

        return instanceId;
    }

    /// <summary>
    /// The invariant, asserted two independent ways.
    /// <para>
    /// First from the flow's own version marks: <c>versionSeenByFanOut</c> is the head the batch
    /// was about to supersede, <c>versionAfterFanOut</c> is what the next task saw, and exactly one
    /// patch must separate them. Second from the public data endpoint, which answers 200 with a
    /// null <c>data</c> body for a version that does not exist (it does NOT 404, and it does NOT
    /// fall back to latest): the version the batch wrote must resolve, and the version two patches
    /// past it must not — with N per-item writes it would.
    /// </para>
    /// </summary>
    private async Task AssertOneVersionForTheBatchAsync(string instanceId, int itemCount)
    {
        var attributes = await GetAttributesAsync(Workflow, instanceId);

        var seen = RequireVersion(attributes, "versionSeenByFanOut");
        var after = RequireVersion(attributes, "versionAfterFanOut");

        Assert.True(seen.Major == after.Major && seen.Minor == after.Minor,
            $"the batch changed the major/minor line ({seen.Raw} → {after.Raw}); the single-write " +
            "arithmetic below only holds along one patch line");

        Assert.True(after.Patch - seen.Patch == 1,
            $"the fan-out batch produced {after.Patch - seen.Patch} instance-data versions " +
            $"({seen.Raw} → {after.Raw}) for {itemCount} items. Exactly ONE is required: the batch " +
            "is single-writer by design — item handlers run with SuppressDataApply on discarded " +
            "branch contexts and only OutputHandler's data is merged. A count matching the item " +
            "count means the per-item suppression is gone.");

        // Independent corroboration through the public API.
        var written = after.Raw;
        var head = $"{after.Major}.{after.Minor}.{after.Patch + 1}";   // the stamp task's own row
        var beyondHead = $"{after.Major}.{after.Minor}.{after.Patch + 2}";

        Assert.True(await DataVersionExistsAsync(instanceId, written),
            $"the version the batch wrote ({written}) did not resolve on the data endpoint");
        Assert.True(await DataVersionExistsAsync(instanceId, head),
            $"the head version ({head}) did not resolve on the data endpoint");
        Assert.False(await DataVersionExistsAsync(instanceId, beyondHead),
            $"a version beyond the head ({beyondHead}) resolved — the batch wrote more rows than " +
            "the single-write invariant allows");
    }

    /// <summary>
    /// True when the instance carries the named data version.
    /// <para>
    /// The orchestration data endpoint answers <c>200</c> with <c>"data": null</c> for an unknown
    /// version rather than <c>404</c> (only the monitoring host 404s), so presence has to be read
    /// off the body, not the status code. Note the runtime's version matching is prefix-tolerant —
    /// <c>"1"</c> and <c>"1.0"</c> resolve to the highest matching line — which is exactly why every
    /// probe here passes a full three-part version.
    /// </para>
    /// </summary>
    private async Task<bool> DataVersionExistsAsync(string instanceId, string version)
    {
        var url = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/data?version={version}";
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers());

        Assert.True(status == HttpStatusCode.OK,
            $"data?version={version} answered {(int)status}: {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("data", out var data) &&
               data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static JsonElement[] ReadResults(JsonElement attributes)
    {
        Assert.True(attributes.TryGetProperty("documentResults", out var results),
            "instance data carried no 'documentResults' — the join's resultKey never landed");
        return results.EnumerateArray().ToArray();
    }

    private static JsonElement Summary(JsonElement attributes)
    {
        Assert.True(attributes.TryGetProperty("documentResultsSummary", out var summary),
            "instance data carried no '{resultKey}Summary' — the branch conditions have nothing to read");
        return summary;
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

    private static SemVer RequireVersion(JsonElement attributes, string key)
    {
        Assert.True(attributes.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String,
            $"instance data carried no '{key}' — the version marks the invariant is measured with " +
            "are missing, so the assertion below would be vacuous rather than passing");

        var raw = value.GetString() ?? "";
        var parts = raw.Split('.');

        var major = 0;
        var minor = 0;
        var patch = 0;
        var parsed = parts.Length >= 3 &&
                     int.TryParse(parts[0], out major) &&
                     int.TryParse(parts[1], out minor) &&
                     // A version row may carry a prerelease/build suffix (1.0.3-rc+build); the
                     // patch number is everything before the first '-' or '+'.
                     int.TryParse(parts[2].Split('-', '+')[0], out patch);

        Assert.True(parsed, $"'{key}' was not a parseable version: '{raw}'");

        return new SemVer(raw, major, minor, patch);
    }

    private readonly record struct SemVer(string Raw, int Major, int Minor, int Patch);
}
