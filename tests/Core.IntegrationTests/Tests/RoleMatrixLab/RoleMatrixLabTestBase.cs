using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Shared plumbing for the <c>role-matrix-lab</c> authorization tests.
/// <para>
/// Every assertion here is about a STATUS CODE or about which keys appear in a body, so almost
/// nothing goes through the SDK client: it surfaces the parsed body, not the response code, and a
/// 403 is exactly what most of these tests are looking for. Reads therefore go out over
/// <see cref="WorkflowTestBase.SendRawAsync"/>, which hands back both.
/// </para>
/// <para>
/// The flow is driven by the APPROVER. That is not arbitrary: <c>review</c> declares its own
/// <c>queryRoles</c> that DENY the maker, so a maker can start a case and then no longer read it.
/// The approver is the only role that passes the gate in both <c>intake</c> and <c>review</c>.
/// </para>
/// </summary>
public abstract class RoleMatrixLabTestBase : WorkflowTestBase
{
    protected const string Workflow = "role-matrix-lab";

    // ── roles ────────────────────────────────────────────────────────────────
    // Deliberately the morph-idm namespace: when the caller-role provider is switched from
    // `default` to `morph-idm`, these same role strings must arrive from the IDM operation set
    // instead of the `role` header, and every assertion in this suite must still hold.
    protected const string Maker = "morph-idm.maker";
    protected const string Approver = "morph-idm.approver";
    protected const string Auditor = "morph-idm.auditor";
    protected const string Viewer = "morph-idm.viewer";

    /// <summary>A caller holding no roles at all — neither header spelling is sent.</summary>
    protected const string? NoRole = null;

    protected RoleMatrixLabTestBase(VNextTestEnvironment environment) : base(environment) { }

    // ── lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a case and leaves it in <c>intake</c>. Started AS THE APPROVER so the same caller can
    /// drive it the whole way; tests that care about the starter's identity (the
    /// <c>$InstanceStarter</c> grant on <c>escalate</c>) start their own case explicitly.
    /// </summary>
    protected async Task<string> StartCaseAsync(string tag, string? roles = Approver)
    {
        var instanceId = await StartAsync(Workflow, new { caseRef = $"{tag}-{Guid.NewGuid():N}"[..24] }, roles);
        await WaitUntilSettledAsync(Workflow, instanceId, roles ?? Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, roles ?? Approver);
        return instanceId;
    }

    /// <summary>Starts a case and drives it into <c>review</c>.</summary>
    protected async Task<string> StartCaseInReviewAsync(string tag, string? startRoles = Approver)
    {
        var instanceId = await StartCaseAsync(tag, startRoles);
        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);
        return instanceId;
    }

    // ── raw reads (status code preserved) ────────────────────────────────────

    /// <summary>
    /// Calls a built-in or custom instance function and returns the status alongside the parsed
    /// body. A non-2xx answer yields <c>default</c> for the body — check the status first.
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> CallInstanceFunctionAsync(
        string instanceId, string function, string? roles, string? query = null)
    {
        var url = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/{function}"
                  + (query is null ? "" : "?" + query);

        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: HeadersFor(roles));
        return (status, Parse(body));
    }

    /// <summary>
    /// The <c>authorize</c> function. Exactly one target may be supplied — a transition key, a
    /// function key, or the queryRoles check — and the runtime rejects a call that names none or
    /// more than one, which is itself worth a test.
    /// </summary>
    protected Task<(HttpStatusCode Status, JsonElement Body)> AuthorizeAsync(
        string instanceId,
        string? roles,
        string? transitionKey = null,
        string? functionKey = null,
        bool queryRoles = false,
        string? roleParameter = null)
    {
        var parts = new List<string>();
        if (transitionKey is not null) parts.Add($"transitionKey={transitionKey}");
        if (functionKey is not null) parts.Add($"functionKey={functionKey}");
        if (queryRoles) parts.Add("queryRoles=true");
        if (roleParameter is not null) parts.Add($"role={roleParameter}");

        return CallInstanceFunctionAsync(
            instanceId, "authorize", roles, parts.Count == 0 ? null : string.Join("&", parts));
    }

    /// <summary>True when <c>authorize</c> answered 200 (allowed); false when it answered 403.</summary>
    protected async Task<bool> IsAuthorizedAsync(
        string instanceId, string? roles, string? transitionKey = null,
        string? functionKey = null, bool queryRoles = false)
    {
        var (status, _) = await AuthorizeAsync(instanceId, roles, transitionKey, functionKey, queryRoles);

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"authorize answered {(int)status}, which is neither allowed (200) nor denied (403)");

        return status == HttpStatusCode.OK;
    }

    // ── state function projections ───────────────────────────────────────────

    /// <summary>
    /// The transition keys the state function offers this caller. Scheduled entries are dropped —
    /// they are not caller-triggerable and are not role-filtered, so they would only add noise.
    /// </summary>
    protected async Task<IReadOnlyList<string>> AvailableTransitionKeysAsync(
        string instanceId, string? roles)
    {
        var (status, body) = await CallInstanceFunctionAsync(instanceId, "state", roles);
        Assert.Equal(HttpStatusCode.OK, status);

        if (!body.TryGetProperty("transitions", out var transitions)) return [];

        var keys = new List<string>();
        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("kind", out var kind) && kind.GetString() == "scheduled")
                continue;
            if (transition.TryGetProperty("name", out var name) && name.GetString() is { } key)
                keys.Add(key);
        }

        return keys;
    }

    /// <summary>The <c>kind</c> discriminator the state function reports for a listed transition.</summary>
    protected async Task<string?> TransitionKindAsync(string instanceId, string? roles, string key)
    {
        var (status, body) = await CallInstanceFunctionAsync(instanceId, "state", roles);
        Assert.Equal(HttpStatusCode.OK, status);

        if (!body.TryGetProperty("transitions", out var transitions)) return null;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("name", out var name) && name.GetString() == key)
                return transition.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
        }

        return null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Headers for a caller, including the role-less case. <see cref="WorkflowTestBase.Headers"/>
    /// already omits the role headers when given null, but going through this one keeps the intent
    /// visible at the call sites that are specifically testing a caller with no roles.
    /// </summary>
    protected static Dictionary<string, string> HeadersFor(string? roles) => Headers(roles);

    private static JsonElement Parse(string body) =>
        string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>True when the response body carries the named property.</summary>
    protected static bool Has(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out _);

    /// <summary>
    /// Instance attributes as returned by the DATA function for this caller — the surface that
    /// applies master-schema <c>x-roles</c> pruning. <c>GetAttributesAsync</c> reads the instance
    /// endpoint instead, so it is not interchangeable here.
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Attributes)> GetDataAttributesAsync(
        string instanceId, string? roles)
    {
        var (status, body) = await CallInstanceFunctionAsync(instanceId, "data", roles);
        if (status != HttpStatusCode.OK) return (status, default);

        // The data function answers either the attributes themselves or an envelope carrying them.
        return (status, body.TryGetProperty("attributes", out var attributes) ? attributes : body);
    }
}
