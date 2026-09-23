using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// Shared plumbing for the <c>authorization-chain-lab</c> suite.
/// <para>
/// Everything here is about a STATUS CODE, so reads go out over
/// <see cref="WorkflowTestBase.SendRawAsync"/> rather than the SDK client — the client surfaces the
/// parsed body and swallows the code, and a 403 is what most of these tests are looking for.
/// </para>
/// <para>
/// The chain assembles itself: starting the root walks automatically to the deepest leaf, so a test
/// only waits for the correlation map to reach depth two. Every assertion below is then about what
/// <c>authorize</c> says versus what the read surfaces actually do — those two answering differently
/// is the defect this lab exists to catch.
/// </para>
/// </summary>
public abstract class AuthorizationChainLabTestBase : WorkflowTestBase
{
    // Three-level chain: root → mid → leaf.
    protected const string Root = "authorization-chain-lab-root";
    protected const string Mid = "authorization-chain-lab-mid";
    protected const string Leaf = "authorization-chain-lab-leaf";

    // Two-level pair, same terminal child. The override mechanism is only reachable on an instance
    // with NO active SubFlow of its own (see StartTwoLevelAsync), which is why it lives here.
    protected const string RootPlain = "authorization-chain-lab-root-plain";
    protected const string RootNarrow = "authorization-chain-lab-root-narrow";
    protected const string MidTerminal = "authorization-chain-lab-mid-terminal";

    // ── roles ────────────────────────────────────────────────────────────────
    // Each role is chosen to fail at exactly one level, so a wrong verdict names its own cause.
    //   reader     : root ✓   root's override of mid ✗
    //   admin      : root ✓   root's override of mid ✓   mid's override of leaf ✗
    //   leafAdmin  : root ✗                                (mid's override of leaf would pass)
    //   midOnly    : root ✗   mid's own queryRoles ✓       (only reachable via root-plain)
    protected const string Reader = "chain.reader";
    protected const string Admin = "chain.admin";
    protected const string LeafAdmin = "chain.leaf-admin";
    protected const string MidOnly = "chain.mid-only";

    /// <summary>
    /// The propagation probe: granted by the ROOT's override of the mid and by the mid's own grants,
    /// and deliberately absent from the mid's override of the leaf. If an ancestor's override ever
    /// travelled past its direct child, this role would be admitted at the leaf.
    /// </summary>
    protected const string MidAdmin = "chain.mid-admin";

    /// <summary>A caller holding no roles at all — neither header spelling is sent.</summary>
    protected const string? NoRole = null;

    protected AuthorizationChainLabTestBase(VNextTestEnvironment environment) : base(environment) { }

    // ── lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a chain and waits until the root holds an active correlation to the mid — which, since
    /// the mid descends automatically too, means the leaf is live as well.
    /// </summary>
    /// <remarks>
    /// Started as <see cref="Admin"/>: it is the only role that passes the root's own gate AND the
    /// root's override of the mid, so the same caller can drive the fixture into place. Tests that
    /// care about a narrower role read with that role afterwards; they do not need to start with it.
    /// </remarks>
    /// <summary>
    /// Starts a two-level chain (root → terminal mid) and waits for the correlation.
    /// <para>
    /// Separate from <see cref="StartChainAsync"/> for a measured reason: a parent's stamped override
    /// is looked up by <c>EffectiveState</c>, which for an instance that ITSELF has an active SubFlow
    /// is a descendant's state key — so the override never matches there and the gate falls back to
    /// the workflow root's grants. Verified on the bench: with the root narrowing the mid to
    /// chain.admin, chain.mid-only still read the mid 200. The override tests therefore run against a
    /// child with nothing beneath it, where the mechanism is reachable.
    /// </para>
    /// </summary>
    protected async Task<(string RootId, string ChildId)> StartTwoLevelAsync(string workflow)
    {
        var rootId = await StartAsync(workflow, new { chainRef = $"two-{Guid.NewGuid():N}"[..24] }, Admin);
        await AssertNotFaultedAsync(workflow, rootId, Admin);

        string? childId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(workflow, rootId, Admin);
            return subs.TryGetValue(MidTerminal, out childId);
        }, $"{workflow} {rootId} should open a correlation to {MidTerminal}");

        return (rootId, childId!);
    }

    protected async Task<ChainInstances> StartChainAsync(string workflow = Root)
    {
        var rootId = await StartAsync(workflow, new { chainRef = $"chain-{Guid.NewGuid():N}"[..24] }, Admin);
        await AssertNotFaultedAsync(workflow, rootId, Admin);

        string? midId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(workflow, rootId, Admin);
            return subs.TryGetValue(Mid, out midId);
        }, $"{workflow} {rootId} should open a correlation to {Mid}");

        string? leafId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(Mid, midId!, Admin);
            return subs.TryGetValue(Leaf, out leafId);
        }, $"{Mid} {midId} should open a correlation to {Leaf}");

        return new ChainInstances(workflow, rootId, midId!, leafId!);
    }

    // ── authorize ────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls <c>authorize</c> against one level of the chain. Exactly one target may be supplied;
    /// the runtime rejects a call naming none or more than one.
    /// </summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> AuthorizeAsync(
        string workflow,
        string instanceId,
        string? roles,
        string? transitionKey = null,
        bool queryRoles = false,
        bool ack = false,
        IDictionary<string, string>? extraHeaders = null,
        string? roleParameter = null)
    {
        var parts = new List<string>();
        if (transitionKey is not null) parts.Add($"transitionKey={transitionKey}");
        if (queryRoles) parts.Add("queryRoles=true");
        if (ack) parts.Add("ack=true");
        // The `role` QUERY parameter — a different channel from the `role`/`x-roles` headers, and the
        // one an authority provider must not honour.
        if (roleParameter is not null) parts.Add($"role={Uri.EscapeDataString(roleParameter)}");

        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/authorize"
                  + (parts.Count == 0 ? "" : "?" + string.Join("&", parts));

        var headers = Headers(roles);
        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders) headers[k] = v;

        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: headers);
        return (status, Parse(body));
    }

    /// <summary>
    /// True when <c>authorize</c> answered 200, false when it answered 403.
    /// <para>
    /// The body carries the verdict on BOTH statuses (<c>{"allowed": …}</c>), so a consumer reading
    /// only the 200 turns every refusal into "no answer". The assertion below is what keeps this
    /// helper from quietly hiding a 404 or a 500 as a denial.
    /// </para>
    /// </summary>
    protected async Task<bool> IsAuthorizedAsync(
        string workflow, string instanceId, string? roles,
        string? transitionKey = null, bool queryRoles = false, bool ack = false,
        IDictionary<string, string>? extraHeaders = null, string? roleParameter = null)
    {
        var (status, body) = await AuthorizeAsync(
            workflow, instanceId, roles, transitionKey, queryRoles, ack, extraHeaders, roleParameter);

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"authorize answered {(int)status}, which is neither allowed (200) nor denied (403)");

        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("allowed", out var allowed))
        {
            Assert.Equal(status == HttpStatusCode.OK, allowed.GetBoolean());
        }

        return status == HttpStatusCode.OK;
    }

    // ── read surfaces ────────────────────────────────────────────────────────

    /// <summary>Calls a built-in instance function and preserves the status code.</summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> CallFunctionAsync(
        string workflow, string instanceId, string function, string? roles, string? query = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/{function}"
                  + (query is null ? "" : "?" + query);

        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers(roles));
        return (status, Parse(body));
    }

    /// <summary>The instance-scoped routes that are not `functions/…` but carry the same gate.</summary>
    protected async Task<(HttpStatusCode Status, JsonElement Body)> CallInstanceRouteAsync(
        string workflow, string instanceId, string route, string? roles)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/{route}";
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers(roles));
        return (status, Parse(body));
    }

    protected static JsonElement Parse(string body) =>
        string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>One level of an assembled chain.</summary>
    protected sealed record ChainInstances(string RootWorkflow, string RootId, string MidId, string LeafId);
}
