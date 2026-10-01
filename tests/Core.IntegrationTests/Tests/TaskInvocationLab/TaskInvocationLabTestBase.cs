using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.ErrorBoundaryLab;

namespace Core.IntegrationTests.Tests.TaskInvocationLab;

/// <summary>
/// Plumbing for the task-invocation-lab scenario (vnext issue #1007: Http, DaprService, Soap,
/// StateStore and CacheAside all run in-process on Orchestration now, behind
/// <c>Workflow:TaskInvocation</c> routing that can flip each type back to the Execution service
/// one key at a time — see <c>docs/runtime/task-invocation-routing.md</c> in the vnext repo).
/// <para>
/// Mirrors <c>Tests/ErrorBoundaryLab/ErrorBoundaryLabTestBase</c>: <c>GET .../instances/{id}/incidents</c>
/// has no SDK method, so the incident reads here are raw HTTP, same as that class. This class
/// reuses <see cref="ErrorBoundaryLab.MockLabAdminClient"/> rather than duplicating it — the lab's
/// only stateful MockLab need is "is it up", and that lab's client already answers exactly that
/// (this scenario has no sequential mock, so <c>ResetSequenceAsync</c> is simply unused).
/// </para>
/// <para>
/// Every case here starts a fresh instance on <c>ready</c> and fires exactly one case transition
/// (<c>case-http-ok</c>, <c>case-dapr-ok</c>, …) — there is no multi-step flow to drive, unlike
/// error-boundary-lab's <c>shouldFail</c> parked-instance pattern.
/// </para>
/// </summary>
public abstract class TaskInvocationLabTestBase : WorkflowTestBase
{
    protected const string Workflow = "task-invocation-lab";
    protected const string ReadyState = "ready";

    /// <summary>An orchestrator client this class owns, for the incident endpoints the SDK has no method for.</summary>
    protected HttpClient RawClient { get; }

    protected TaskInvocationLabTestBase(VNextTestEnvironment environment) : base(environment)
    {
        RawClient = new HttpClient
        {
            BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/")
        };
    }

    // ── running a case ───────────────────────────────────────────────────────

    /// <summary>Starts a fresh instance and waits until it is parked on <c>ready</c>.</summary>
    protected async Task<string> StartInstanceAsync()
    {
        var instanceId = await StartAsync(Workflow, new { });
        await WaitUntilSettledAsync(Workflow, instanceId);
        return instanceId;
    }

    /// <summary>
    /// Runs a whole case: fresh instance on <c>ready</c>, one case transition, settled. Tolerates
    /// the instance faulting afterwards — the SOAP-fault case's task-level Abort boundary faults it
    /// by design, and only the initial accept has to succeed here, exactly like
    /// <c>ErrorBoundaryLabTestBase.RunCaseAsync</c>.
    /// </summary>
    protected async Task<string> RunCaseAsync(string caseKey)
    {
        var instanceId = await StartInstanceAsync();
        await RunAcceptedAsync(Workflow, instanceId, caseKey);
        return instanceId;
    }

    /// <summary>
    /// The precondition for every case whose task calls MockLab (Http, DaprService, Soap, and
    /// CacheAside's source task) — the two StateStore cases talk to the Dapr state component
    /// directly and need no MockLab guard. Call from a <c>[SkippableFact]</c>:
    /// <code>Skip.If(!await IsMockLabUpAsync(), "…")</code>
    /// </summary>
    protected static async Task<bool> IsMockLabUpAsync()
    {
        using var mocklab = new MockLabAdminClient();
        return await mocklab.IsUpAsync();
    }

    /// <summary>The MockLab base URL, for the skip message.</summary>
    protected static string MockLabBaseUrl()
    {
        using var mocklab = new MockLabAdminClient();
        return mocklab.BaseUrl;
    }

    /// <summary>
    /// True when <c>case-dapr-ok</c>'s projection shows the Orchestration sidecar could not reach
    /// MockLab's Dapr app-id AT ALL — a transport/name-resolution failure — rather than a genuine
    /// business response from the callee. Confirmed by curling the Orchestration sidecar's own
    /// invoke endpoint directly (<c>POST /v1.0/invoke/mocklab/method/api/til/ok</c> against its
    /// <c>dapr-http-port</c>): it answers <c>500 {"errorCode":"ERR_DIRECT_INVOKE","message":"failed
    /// to invoke, id: mocklab, err: couldn't find service: mocklab"}</c> even though
    /// <c>til-dapr-ok</c>'s <c>appId</c>/<c>methodName</c> match the working
    /// <c>core/Tasks/money-transfer/get-accounts-dapr.json</c> pattern exactly and both sidecars
    /// report <c>Initialized name resolution to mdns</c> on the same Docker network — i.e. this is
    /// the local mDNS-across-sidecars gap the vnext repo's cross-domain-lab notes already document,
    /// not a task/component misconfiguration.
    /// <see cref="DaprServiceInvocation"/> (in the vnext repo) never throws on this: the sidecar's
    /// own error response comes back as an ordinary (non-2xx) HTTP response, so it lands as
    /// <c>tilStatusCode: 500</c> with the sidecar's error JSON parsed into <c>tilData</c> — a
    /// business-shaped path, not a fault, which is why the instance still lands cleanly instead of
    /// raising an incident.
    /// </summary>
    protected static bool IsDaprServiceUnreachable(System.Text.Json.JsonElement projection) =>
        NullableLong(projection, "tilStatusCode") == 500 &&
        projection.TryGetProperty("tilData", out var data) &&
        data.ValueKind == JsonValueKind.Object &&
        Text(data, "errorCode") == "ERR_DIRECT_INVOKE";

    // ── incident surfaces (subset of ErrorBoundaryLabTestBase — see its remarks for why raw HTTP) ──

    /// <summary>The incident items of an instance, newest first. Fails the test on a non-200.</summary>
    protected async Task<List<JsonElement>> GetIncidentItemsAsync(string instanceId, int pageSize = 20)
    {
        var url = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/incidents?page=1&pageSize={pageSize}";
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers());
        Assert.True(status == HttpStatusCode.OK, $"incident history refused with {(int)status}: {body}");

        return Parse(body).GetProperty("items").EnumerateArray().ToList();
    }

    /// <summary>The newest incident carrying boundary metadata — see ErrorBoundaryLabTestBase.BoundaryIncident.</summary>
    protected static JsonElement BoundaryIncident(List<JsonElement> items)
    {
        var match = items.FirstOrDefault(i => i.TryGetProperty("boundaryAction", out var action) &&
                                              action.ValueKind == JsonValueKind.String);

        Assert.True(match.ValueKind == JsonValueKind.Object,
            "no incident carried boundaryAction — the boundary never resolved an action. " +
            $"Incidents: {string.Join(" | ", items.Select(i => Text(i, "errorCode")))}");

        return match;
    }

    /// <summary><c>metadata.incident</c> of <c>GET .../instances/{id}</c>, or null when absent.</summary>
    protected async Task<JsonElement?> GetIncidentMetadataAsync(string instanceId)
    {
        var response = await Api.GetInstanceAsync(Workflow, instanceId, Headers());
        var metadata = response.Body.GetProperty("metadata");

        return metadata.TryGetProperty("incident", out var incident) && incident.ValueKind != JsonValueKind.Null
            ? incident
            : null;
    }

    // ── waiting ──────────────────────────────────────────────────────────────

    /// <summary>Waits for the instance to fault — the inverse of a settle-and-succeed wait.</summary>
    protected Task WaitUntilFaultedAsync(string instanceId, TimeSpan? timeout = null) =>
        WaitUntilAsync(
            async () =>
            {
                var (_, status) = await GetInstanceStateAsync(Workflow, instanceId);
                if (status == "F") return true;

                Assert.True(status is not ("C" or "P"),
                    $"instance settled '{status}' instead of faulting — " + await DescribeAsync(Workflow, instanceId));
                return false;
            },
            $"{Workflow}/{instanceId} never faulted",
            timeout);

    // ── projection / assertion helpers ──────────────────────────────────────

    /// <summary>Reads the <c>til*</c> projection <c>TilResultProjection.csx</c> writes into instance data.</summary>
    protected Task<JsonElement> GetProjectionAsync(string instanceId) => GetAttributesAsync(Workflow, instanceId);

    protected static string? NullableString(JsonElement attributes, string property) =>
        attributes.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    protected static long? NullableLong(JsonElement attributes, string property) =>
        attributes.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    protected static bool BoolOrFalse(JsonElement attributes, string property) =>
        attributes.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    protected static int ArrayLength(JsonElement attributes, string property) =>
        attributes.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    /// <summary>The keys of <c>tilMetadataKeys</c>, for a set comparison against the expected shape.</summary>
    protected static HashSet<string> MetadataKeySet(JsonElement attributes) =>
        attributes.TryGetProperty("tilMetadataKeys", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.GetString() ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            $"expected '{property}' to be absent: {because}.");

    private static JsonElement Parse(string body) =>
        string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
}
