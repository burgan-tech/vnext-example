using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// The incident lifecycle as a client sees it: a task fails, the instance faults, and the same
/// incident is readable from three places — the state function's <c>incident</c> block, the paged
/// <c>GET .../instances/{id}/incidents</c> history, and <c>metadata.incident</c> on the instance
/// itself. Then a retry resolves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Incidents moved out of the <c>Instances.Incidents</c> jsonb column into
/// their own table, and the state function grew an <c>incident</c> block plus a history endpoint
/// (vnext <c>feature/incident-table</c>, issue #865). Every assertion here is about a surface that
/// did not exist before that change; unit tests cover the storage, only a running instance covers
/// the contract.
/// </para>
/// <para>
/// <b>One failure, one incident.</b> This lab originally measured TWO incidents per abort — the
/// boundary's verdict plus a bare pipeline row for the fault itself — because the task step saved
/// the instance BEFORE recording the incident, so the fault path's reload still saw
/// <c>HasActiveIncident = false</c> and added its own fallback row. The three task steps now record
/// the incident first and save it together with the flag, and these tests hold that line: an abort
/// leaves exactly one row and <c>incident.active</c> is the boundary's verdict, complete with
/// <c>boundaryAction</c>.
/// </para>
/// <para>
/// <b>A retry that faults again stays faulted.</b> The retry request used to load the aggregate
/// tracked in the ambient request scope, so the ambient commit wrote a stale <c>Active</c> over the
/// <c>Faulted</c> the inner scope had persisted — leaving an instance that looked healthy, had not
/// finished its work, and could never be retried again. Retry now loads no-tracking and unfaults
/// with a compare-and-set, so the persisted status agrees with the response and a second retry is
/// accepted.
/// </para>
/// </remarks>
public class IncidentLifecycleTests : ErrorBoundaryLabTestBase
{
    public IncidentLifecycleTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task AFailedTask_RecordsAnIncidentWithTheFailureDetail()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-task-abort");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var items = await GetIncidentItemsAsync(Workflow, instanceId);
        var boundary = BoundaryIncident(items);

        Assert.Equal("Task:Script:eb-script-fail-task:500", Text(boundary, "errorCode"));
        Assert.Equal("Task", Text(boundary, "errorLayer"));
        Assert.Equal(500, Number(boundary, "statusCode"));
        Assert.Equal("eb-script-fail-task", Text(boundary, "task"));
        Assert.Equal(HubState, Text(boundary, "state"));
        Assert.Equal("case-task-abort", Text(boundary, "transition"));
        Assert.False(Flag(boundary, "isResolved"), "the incident is still open — nothing resolved it");
        Assert.NotEqual("-", Text(boundary, "traceId"));
        Assert.Contains("deliberate script failure", Text(boundary, "message"));

        // The stack trace is deliberately not part of this surface; operators read it from the
        // Monitor API instead.
        AssertAbsent(boundary, "stackTrace", "the client-facing history never carries stack traces");
    }

    [Fact]
    public async Task AnAbort_RecordsExactlyOneIncident_TheBoundaryVerdict()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-task-abort");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var items = await GetIncidentItemsAsync(Workflow, instanceId);

        // The regression this guards: the task step commits the incident and the HasActiveIncident
        // flag in one save, so the fault path's reload sees a live incident and skips its fallback
        // row. If a bare `ErrorBoundaryAbort` row reappears here, that ordering broke again.
        var single = Assert.Single(items);

        Assert.Equal("Abort", Text(single, "boundaryAction"));
        Assert.Equal("Task", Text(single, "boundaryLevel"));
        Assert.Equal("eb-script-fail-task", Text(single, "task"));
        Assert.Equal(500, Number(single, "statusCode"));
        Assert.NotEqual("Pipeline", Text(single, "errorLayer"));
        Assert.DoesNotContain(items, i => Text(i, "errorCode") == "ErrorBoundaryAbort");
    }

    [Fact]
    public async Task TheStateFunction_CarriesLinksAndTheActiveLinkAnswers()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-task-abort");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var incident = await GetStateIncidentAsync(Workflow, instanceId);

        Assert.True(Flag(incident, "hasActiveIncident"));
        Assert.Equal(
            $"/api/v1/core/workflows/{Workflow}/instances/{instanceId}/incidents",
            Text(incident.GetProperty("history"), "href"));

        // The block is links only: no incident field may leak into the state body.
        var active = incident.GetProperty("active");
        Assert.Equal(
            $"/api/v1/core/workflows/{Workflow}/instances/{instanceId}/incidents/active",
            Text(active, "href"));
        foreach (var leaked in new[] { "id", "errorCode", "message", "state", "transition", "task", "stackTrace" })
            AssertAbsent(active, leaked, "the state body carries a link, not the incident");

        // Follow the advertised link rather than rebuilding it, so this pins the contract the client
        // actually uses. The detail behind it is the boundary's verdict.
        var (status, detail) = await FollowAsync(Text(active, "href"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Abort", Text(detail, "boundaryAction"));
        Assert.Equal("eb-script-fail-task", Text(detail, "task"));
        Assert.Equal(HubState, Text(detail, "state"));
        Assert.Equal("case-task-abort", Text(detail, "transition"));
        AssertAbsent(detail, "stackTrace", "stack traces stay on the Monitor API");
    }

    [Fact]
    public async Task TheStateBlockIsPresentEvenWhenNothingHasFailed()
    {
        var instanceId = await StartCaseAsync(Workflow, "clean");

        var incident = await GetStateIncidentAsync(Workflow, instanceId);

        Assert.False(Flag(incident, "hasActiveIncident"));
        AssertAbsent(incident, "active", "no incident is open, so no link is advertised");
        Assert.NotEqual("-", Text(incident.GetProperty("history"), "href"));

        // metadata.incident follows the same shape and is present even with an empty history.
        var metadata = await GetIncidentMetadataAsync(Workflow, instanceId);
        Assert.NotNull(metadata);
        Assert.False(Flag(metadata!.Value, "hasActiveIncident"));
        AssertAbsent(metadata.Value, "active", "no incident is open");
        Assert.NotEqual("-", Text(metadata.Value.GetProperty("history"), "href"));
        Assert.Empty(await GetIncidentItemsAsync(Workflow, instanceId));

        // The link is not advertised, but a client that guesses the URL gets an honest 404.
        var (status, _) = await GetActiveIncidentAsync(Workflow, instanceId);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task InstanceMetadata_CarriesTheSameBlockAsTheStateFunction()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-task-abort");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var metadata = await GetIncidentMetadataAsync(Workflow, instanceId);
        Assert.NotNull(metadata);
        var block = metadata!.Value;

        // One shape across both surfaces: a client learns it once.
        var state = await GetStateIncidentAsync(Workflow, instanceId);
        Assert.True(Flag(block, "hasActiveIncident"));
        Assert.Equal(Text(state.GetProperty("active"), "href"), Text(block.GetProperty("active"), "href"));
        Assert.Equal(Text(state.GetProperty("history"), "href"), Text(block.GetProperty("history"), "href"));

        // Nothing is embedded any more: totalCount and the flat href are gone, and `history` is a
        // link object rather than the newest-five array it used to be.
        AssertAbsent(block, "totalCount", "metadata.incident carries links, not counts");
        AssertAbsent(block, "href", "the flat href became history.href");
        Assert.Equal(JsonValueKind.Object, block.GetProperty("history").ValueKind);
    }

    [Fact]
    public async Task TheHistoryPages()
    {
        // An abort leaves exactly one incident, so a page boundary needs a second FAILURE rather
        // than a second row for the same one: fault, retry with the failure still armed, fault
        // again. That also makes this test depend on the retry path staying faulted.
        var instanceId = await RunCaseAsync(Workflow, "case-retry-endpoint");
        await WaitUntilFaultedAsync(Workflow, instanceId);
        await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = true } });
        await WaitUntilFaultedAsync(Workflow, instanceId);

        Assert.Equal(2, (await GetIncidentItemsAsync(Workflow, instanceId)).Count);

        var (firstStatus, first) = await GetIncidentsAsync(Workflow, instanceId, pageSize: 1);
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal(1, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1, Number(first, "page"));
        Assert.Equal(1, Number(first, "pageSize"));
        Assert.True(Flag(first, "hasNext"), "a second incident exists, so the first page has a next");

        var (secondStatus, second) = await GetIncidentsAsync(Workflow, instanceId, page: 2, pageSize: 1);
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.False(Flag(second, "hasNext"), "the second page is the last one");

        var firstId = Text(first.GetProperty("items")[0], "id");
        var secondId = Text(second.GetProperty("items")[0], "id");
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task RetryIsRefusedWhileTheInstanceIsHealthy()
    {
        var instanceId = await StartCaseAsync(Workflow, "retry-guard");

        var (status, body) = await RetryAsync(Workflow, instanceId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Instance:100027", body);
    }

    [Fact]
    public async Task RetryingWithTheFailureStillArmed_RecordsANewIncidentAndReportsFaulted()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-retry-endpoint");
        await WaitUntilFaultedAsync(Workflow, instanceId);
        var before = await GetIncidentItemsAsync(Workflow, instanceId);

        var (status, body) = await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = true } });

        // A retry that faults again is still a successful REQUEST — the outcome is in the body.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"status\":\"F\"", body);
        Assert.Contains("retriedTransitionId", body);

        // The response's F is durable now, so waiting for the fault is the right wait.
        await WaitUntilFaultedAsync(Workflow, instanceId);
        var after = await GetIncidentItemsAsync(Workflow, instanceId);

        Assert.Equal(1, before.Count);
        Assert.Equal(2, after.Count);

        // The first incident was resolved by the unfault that opened the retry; only the second
        // failure's row is open.
        var open = after.Where(i => !Flag(i, "isResolved")).ToList();
        Assert.Single(open);
    }

    [Fact]
    public async Task AfterARetryThatFailsAgain_TheInstanceStaysFaulted()
    {
        // This lab originally measured the opposite: the retry answered "F" and the instance then
        // settled Active, because the retry loaded the aggregate TRACKED in the ambient request
        // scope and the ambient commit overwrote the Faulted an inner RequiresNew scope had
        // persisted. A client was left with an instance that looked healthy, carried an open
        // incident, had not finished its work, and could never be retried again. Retry now loads
        // no-tracking and unfaults with a compare-and-set, so the ambient scope has nothing to
        // write back.
        var instanceId = await RunCaseAsync(Workflow, "case-retry-endpoint");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var (_, body) = await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = true } });
        Assert.Contains("\"status\":\"F\"", body);

        await WaitUntilFaultedAsync(Workflow, instanceId);
        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);

        Assert.Equal(HubState, state);
        Assert.Equal("F", status);

        var incident = await GetStateIncidentAsync(Workflow, instanceId);
        Assert.True(Flag(incident, "hasActiveIncident"));

        // And, unlike before, the instance is still retryable — the second retry succeeds.
        var (retryStatus, retryBody) = await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = false } });
        Assert.Equal(HttpStatusCode.OK, retryStatus);
        Assert.DoesNotContain("Instance:100027", retryBody);

        var (finalState, finalStatus) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("landed", finalState);
        Assert.NotEqual("F", finalStatus);
    }

    [Fact]
    public async Task RetryingWithTheFailureDisarmed_CompletesTheCase()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-retry-endpoint");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        // The retry body is merged into instance data before OnExecute runs, so the very same task
        // now takes its success path.
        var (status, body) = await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = false } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("\"status\":\"F\"", body);

        var (state, instanceStatus) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("landed", state);
        Assert.NotEqual("F", instanceStatus);

        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.True(attributes.TryGetProperty("scriptRecovered", out var recovered) && recovered.GetBoolean(),
            "the retried task did not take its success path");
    }

    [Fact]
    public async Task ARecoveredInstance_ReportsNoActiveIncident()
    {
        // Two fixes meet here. Unfault now resolves the WHOLE open set rather than the newest row
        // alone, and an abort no longer leaves a second row for it to miss. Before either, a
        // recovered instance kept reporting an active incident whose reason no longer applied —
        // and because HasActiveIncident is fingerprint material, a long-polling client saw it too.
        var instanceId = await RunCaseAsync(Workflow, "case-retry-endpoint");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var (status, _) = await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = false } });
        Assert.Equal(HttpStatusCode.OK, status);

        var (state, instanceStatus) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("landed", state);
        Assert.NotEqual("F", instanceStatus);

        // The history survives — nothing is deleted — but every row in it is closed.
        var items = await GetIncidentItemsAsync(Workflow, instanceId);
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.True(Flag(i, "isResolved"),
            $"a recovered instance still carries an open incident: {Summarize(i)}"));

        var incident = await GetStateIncidentAsync(Workflow, instanceId);
        Assert.False(Flag(incident, "hasActiveIncident"));
        AssertAbsent(incident, "active", "there is no open incident left to summarise");
    }

    [Fact]
    public async Task TheConditionalStateRead_StopsBeing304OnceTheIncidentIsResolved()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-retry-endpoint");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var (status, _, etag) = await GetStateAsync(Workflow, instanceId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrEmpty(etag), "the state function issued no ETag to validate against");

        var (repeat, _, _) = await GetStateAsync(Workflow, instanceId, ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.NotModified, repeat);

        await RetryAsync(Workflow, instanceId, new { attributes = new { shouldFail = false } });
        await WaitUntilSettledAsync(Workflow, instanceId);

        var (afterStatus, _, afterEtag) = await GetStateAsync(Workflow, instanceId, ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.OK, afterStatus);
        Assert.NotEqual(etag, afterEtag);
    }
}
