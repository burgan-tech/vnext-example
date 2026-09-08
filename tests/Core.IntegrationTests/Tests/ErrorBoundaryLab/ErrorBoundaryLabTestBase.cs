using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// Plumbing for the error-boundary lab: run one case on a fresh instance, then read the three
/// surfaces an incident shows up on.
/// <para>
/// The readers are raw HTTP on purpose. <c>GET .../instances/{id}/incidents</c> has no SDK method,
/// the state function's <c>incident</c> block has no typed model, and two of the assertions are
/// about status codes the SDK client turns into exceptions (403 from the queryRoles gate, 304 from
/// a conditional state read). Everything here is a candidate to move into VNext.Testing.Sdk once the
/// shapes stop moving.
/// </para>
/// </summary>
public abstract class ErrorBoundaryLabTestBase : WorkflowTestBase
{
    protected const string Workflow = "error-boundary-lab";
    protected const string GlobalWorkflow = "error-boundary-lab-global";

    /// <summary>The only role <c>zone-secured</c> grants read access to.</summary>
    protected const string ViewerRole = "eb-lab.viewer";

    protected const string HubState = "ready";
    protected const string GlobalHubState = "g-ready";

    /// <summary>
    /// An orchestrator client this class owns. <see cref="WorkflowTestBase"/> keeps its own private
    /// one and exposes it only through helpers that discard the response headers; the ETag test
    /// needs the header, so it needs a client of its own rather than a widened base class.
    /// </summary>
    protected HttpClient RawClient { get; }

    protected ErrorBoundaryLabTestBase(VNextTestEnvironment environment) : base(environment)
    {
        RawClient = new HttpClient
        {
            BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/")
        };
    }

    // ── running a case ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts an instance parked on the hub. <paramref name="shouldFail"/> is read by the script
    /// failure injector; leaving it true is what makes a case fail at all.
    /// </summary>
    protected async Task<string> StartCaseAsync(
        string workflow, string tag, bool shouldFail = true, string? roles = null)
    {
        var instanceId = await StartAsync(
            workflow, new { testId = $"{tag}-{Guid.NewGuid():N}", shouldFail }, roles);

        await WaitUntilSettledAsync(workflow, instanceId, roles);
        return instanceId;
    }

    /// <summary>
    /// Fires a case transition and waits for the instance to stop being Busy. Unlike
    /// <see cref="WorkflowTestBase.RunAcceptedAsync"/> this tolerates the instance faulting — for
    /// most cases here, faulting IS the expected outcome.
    /// </summary>
    protected async Task FireCaseAsync(string workflow, string instanceId, string caseKey, string? roles = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}" +
                  $"/transitions/{caseKey}?sync=false";
        var (status, body) = await SendRawAsync(HttpMethod.Patch, url, new { }, Headers(roles));

        Assert.True((int)status < 400, $"'{caseKey}' was refused with {(int)status}: {body}");
        await WaitUntilSettledAsync(workflow, instanceId, roles);
    }

    /// <summary>Runs a whole case: fresh instance on the hub, one transition, settled.</summary>
    protected async Task<string> RunCaseAsync(
        string workflow, string caseKey, bool shouldFail = true, string? roles = null)
    {
        var instanceId = await StartCaseAsync(workflow, caseKey, shouldFail, roles);
        await FireCaseAsync(workflow, instanceId, caseKey, roles);
        return instanceId;
    }

    // ── incident surfaces ────────────────────────────────────────────────────

    /// <summary>
    /// One page of <c>GET .../instances/{id}/incidents</c>. Returns the status too, because two
    /// tests are about the status alone (403 without the role).
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> GetIncidentsAsync(
        string workflow, string instanceId, string? roles = null, int page = 1, int pageSize = 20)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}" +
                  $"/incidents?page={page}&pageSize={pageSize}";
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers(roles));

        return (status, Parse(body));
    }

    /// <summary>The incident items of an instance, newest first. Fails the test on a non-200.</summary>
    protected async Task<List<JsonElement>> GetIncidentItemsAsync(
        string workflow, string instanceId, string? roles = null, int pageSize = 20)
    {
        var (status, body) = await GetIncidentsAsync(workflow, instanceId, roles, pageSize: pageSize);
        Assert.True(status == HttpStatusCode.OK,
            $"incident history refused with {(int)status}: {body}");

        return body.GetProperty("items").EnumerateArray().ToList();
    }

    /// <summary>
    /// <c>GET .../incidents/active</c>, status included: 404 is a normal answer once the incident is
    /// resolved, so a test has to be able to assert it.
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> GetActiveIncidentAsync(
        string workflow, string instanceId, string? roles = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/incidents/active";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await RawClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, Parse(body));
    }

    /// <summary>
    /// Follows the <c>incident.active.href</c> the block advertises, rather than rebuilding the URL,
    /// so the test proves the advertised link is the one that answers.
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> FollowAsync(
        string href, string? roles = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, href.TrimStart('/'));
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);

        using var response = await RawClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, Parse(body));
    }

    /// <summary>The state function's <c>incident</c> block.</summary>
    protected async Task<JsonElement> GetStateIncidentAsync(
        string workflow, string instanceId, string? roles = null)
    {
        var (status, body, _) = await GetStateAsync(workflow, instanceId, roles);
        Assert.True(status == HttpStatusCode.OK, $"state function refused with {(int)status}: {body}");

        var parsed = Parse(body);
        Assert.True(parsed.TryGetProperty("incident", out var incident),
            "the state body carried no `incident` block — it is supposed to be present always");

        return incident;
    }

    /// <summary>
    /// The raw state function read, including the ETag, so a test can replay it with
    /// <c>If-None-Match</c> and observe a 304.
    /// </summary>
    protected async Task<(HttpStatusCode Status, string Body, string? ETag)> GetStateAsync(
        string workflow, string instanceId, string? roles = null, string? ifNoneMatch = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/state";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        using var response = await RawClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var etag = response.Headers.ETag?.ToString();

        return (response.StatusCode, body, etag);
    }

    /// <summary><c>metadata.incident</c> of <c>GET .../instances/{id}</c>, or null when absent.</summary>
    protected async Task<JsonElement?> GetIncidentMetadataAsync(
        string workflow, string instanceId, string? roles = null)
    {
        var response = await Api.GetInstanceAsync(workflow, instanceId, Headers(roles));
        var metadata = response.Body.GetProperty("metadata");

        return metadata.TryGetProperty("incident", out var incident) && incident.ValueKind != JsonValueKind.Null
            ? incident
            : null;
        // NOTE: the block is always present now (flag + links), so a null return means the runtime
        // stopped emitting it at all — which is a defect, not an empty state.
    }

    /// <summary><c>POST .../instances/{id}/retry</c>, status included — one test asserts a 400.</summary>
    protected Task<(HttpStatusCode Status, string Body)> RetryAsync(
        string workflow, string instanceId, object? body = null, string? roles = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/retry?sync=true";
        return SendRawAsync(HttpMethod.Post, url, body ?? new { }, Headers(roles));
    }

    // ── waiting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the instance to fault. The inverse of
    /// <see cref="WorkflowTestBase.WaitForInstanceStateAsync"/>, which fails fast ON a fault: here a
    /// fault is the expected outcome and reaching a terminal non-faulted status is the failure.
    /// </summary>
    protected Task WaitUntilFaultedAsync(
        string workflow, string instanceId, string? roles = null, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () =>
            {
                var (_, status) = await GetInstanceStateAsync(workflow, instanceId, roles);
                if (status == "F") return true;

                Assert.True(status is not ("C" or "P"),
                    $"instance settled '{status}' instead of faulting — " +
                    await DescribeAsync(workflow, instanceId, roles));
                return false;
            },
            $"{workflow}/{instanceId} never faulted",
            timeout);

    // ── assertions ───────────────────────────────────────────────────────────

    /// <summary>
    /// The newest incident carrying boundary metadata.
    /// <para>
    /// An abort now leaves exactly ONE row and it is the boundary's verdict, so on that path this is
    /// simply <c>items[0]</c>. The search is kept because a retry that faults again, a job-timeout
    /// recovery or a boundary transition that faults on its own can still put a row without boundary
    /// metadata in front of the verdict, and a test asking for the verdict should not have to know
    /// which of those produced the history it is reading.
    /// </para>
    /// </summary>
    protected static JsonElement BoundaryIncident(List<JsonElement> items)
    {
        var match = items.FirstOrDefault(i => i.TryGetProperty("boundaryAction", out var action) &&
                                              action.ValueKind == JsonValueKind.String);

        Assert.True(match.ValueKind == JsonValueKind.Object,
            "no incident carried boundaryAction — the boundary never resolved an action. " +
            $"Incidents: {string.Join(" | ", items.Select(Summarize))}");

        return match;
    }

    /// <summary>A compact incident rendering for failure messages.</summary>
    protected static string Summarize(JsonElement incident) =>
        $"{Text(incident, "errorCode")}/{Text(incident, "errorLayer")} " +
        $"action={Text(incident, "boundaryAction")} level={Text(incident, "boundaryLevel")} " +
        $"task={Text(incident, "task")} resolved={Flag(incident, "isResolved")}";

    /// <summary>A string property, or "-" when the runtime omitted it (nulls are not serialized).</summary>
    protected static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "-"
            : "-";

    protected static int? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;

    protected static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Asserts a property is absent — the runtime omits nulls, so absence IS the null.</summary>
    protected static void AssertAbsent(JsonElement element, string property, string because) =>
        Assert.True(!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null,
            $"expected '{property}' to be absent: {because}. Got {Summarize(element)}");

    private static JsonElement Parse(string body) =>
        string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
}
