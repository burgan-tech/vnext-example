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

    /// <summary>
    /// Actor identity (<c>act_sub</c>) for tests about <c>$InstanceStarter</c>. The runtime records
    /// the starter from <c>act_sub</c> and matches the grant on it; the suite's standard header set
    /// carries only <c>user_reference</c>, so without this nobody is ever "the starter" (the two
    /// reds recorded until 2026-10-05). Scoped to the tests that pass it on purpose: putting
    /// <c>act_sub</c> in every request would turn every caller into the starter and silently change
    /// what the other suites mean by "a caller who did not start the case".
    /// </summary>
    protected const string StarterActor = "u-role-matrix-starter";

    protected RoleMatrixLabTestBase(VNextTestEnvironment environment) : base(environment) { }

    // ── lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a case and leaves it in <c>intake</c>. Started AS THE APPROVER so the same caller can
    /// drive it the whole way; tests that care about the starter's identity (the
    /// <c>$InstanceStarter</c> grant on <c>escalate</c>) start their own case explicitly.
    /// </summary>
    protected async Task<string> StartCaseAsync(string tag, string? roles = Approver, string? actSub = null)
    {
        var body = new { caseRef = $"{tag}-{Guid.NewGuid():N}"[..24] };
        var instanceId = actSub is null
            ? await StartAsync(Workflow, body, roles)
            : (await Api.StartInstanceAsync(Workflow, body, HeadersFor(roles, actSub))).Body.GetProperty("id").GetString()!;
        await WaitUntilSettledAsync(Workflow, instanceId, roles ?? Approver);
        await AssertNotFaultedAsync(Workflow, instanceId, roles ?? Approver);
        return instanceId;
    }

    /// <summary>Starts a case and drives it into <c>review</c>.</summary>
    protected async Task<string> StartCaseInReviewAsync(string tag, string? startRoles = Approver, string? actSub = null)
    {
        var instanceId = await StartCaseAsync(tag, startRoles, actSub);
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
        string instanceId, string function, string? roles, string? query = null, string? actSub = null)
    {
        var url = $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/{function}"
                  + (query is null ? "" : "?" + query);

        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: HeadersFor(roles, actSub));
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
        string? roleParameter = null,
        string? actSub = null)
    {
        var parts = new List<string>();
        if (transitionKey is not null) parts.Add($"transitionKey={transitionKey}");
        if (functionKey is not null) parts.Add($"functionKey={functionKey}");
        if (queryRoles) parts.Add("queryRoles=true");
        if (roleParameter is not null) parts.Add($"role={roleParameter}");

        return CallInstanceFunctionAsync(
            instanceId, "authorize", roles, parts.Count == 0 ? null : string.Join("&", parts), actSub);
    }

    /// <summary>True when <c>authorize</c> answered 200 (allowed); false when it answered 403.</summary>
    protected async Task<bool> IsAuthorizedAsync(
        string instanceId, string? roles, string? transitionKey = null,
        string? functionKey = null, bool queryRoles = false, string? actSub = null)
    {
        var (status, _) = await AuthorizeAsync(instanceId, roles, transitionKey, functionKey, queryRoles, actSub: actSub);

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
        string instanceId, string? roles, string? actSub = null)
    {
        var (status, body) = await CallInstanceFunctionAsync(instanceId, "state", roles, actSub: actSub);
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
    protected static Dictionary<string, string> HeadersFor(string? roles, string? actSub = null)
    {
        var headers = Headers(roles);
        if (actSub is not null) headers["act_sub"] = actSub;
        return headers;
    }

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

        // The data function answers an envelope: { "data": { …attributes… }, "eTag", "entityEtag",
        // "extensions" } (GetInstanceDataOutput). Older shapes carried "attributes". Unwrap whichever is
        // there — reading the envelope's own keys made every field-level assertion look at the wrong
        // object, which is why SchemaFieldVisibilityTests was red since the suite was written.
        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
                return (status, attributes);
            if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                return (status, data);
        }

        return (status, body);
    }
}
