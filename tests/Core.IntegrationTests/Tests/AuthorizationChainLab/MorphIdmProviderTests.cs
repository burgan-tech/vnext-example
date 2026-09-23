using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// The <c>morph-idm</c> caller-role provider, end to end: the roles every authorization decision is
/// made against come from the identity service, and from nowhere else.
/// </summary>
/// <remarks>
/// <para><b>Why a separate run.</b> The provider is chosen once at startup
/// (<c>CallerRoleProvider:Provider</c>) and never varies per request, so it cannot be exercised
/// inside the suite's ordinary run. These tests are skipped unless
/// <c>VNEXT_CALLER_ROLE_PROVIDER=morph-idm</c> says the runtime under test was started that way —
/// silently passing against a <c>default</c>-provider runtime would be worse than not running at
/// all, since they would then prove nothing while looking green.</para>
/// <para><b>What the MockLab seed gives them.</b> <c>morph-idm-roles-collection.json</c> answers
/// <c>GET api/1/morph-idm/functions/get-roles</c>, keyed on the <c>user_reference</c> header the
/// resolver forwards. Four users carry the assertions — <c>idm-admin</c> (a role that passes the
/// whole chain), <c>idm-leaf-only</c> (one that does not), <c>idm-empty</c> (<c>204</c>) and
/// <c>idm-broken</c> (<c>500</c>) — and any other user gets a working default set so the chain can
/// still be assembled.</para>
/// <para><b>What is NOT re-proved here.</b> The conjunction, the overrides and the switch are
/// provider-independent: they consume a role set, they do not decide where it came from. Repeating
/// all 44 under a second provider would cost a full run to re-measure something already measured.
/// What IS provider-specific is which set arrives — and that is the whole subject below.</para>
/// </remarks>
public sealed class MorphIdmProviderTests : AuthorizationChainLabTestBase
{
    public MorphIdmProviderTests(VNextTestEnvironment environment) : base(environment) { }

    private const string AdminUser = "idm-admin";
    private const string LeafOnlyUser = "idm-leaf-only";
    private const string EmptyUser = "idm-empty";
    private const string BrokenUser = "idm-broken";

    /// <summary>
    /// True only when the runtime under test was started with the morph-idm provider. Set it in the
    /// same command that starts that runtime.
    /// </summary>
    private static bool ProviderIsMorphIdm =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("VNEXT_CALLER_ROLE_PROVIDER"),
            "morph-idm",
            System.StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> As(string user) => new() { ["sub"] = user };

    /// <summary>
    /// The decisive one. A caller ASSERTING <c>chain.admin</c> in the <c>role</c>/<c>x-roles</c>
    /// headers, whose identity service answers "no operations", must be refused.
    /// </summary>
    /// <remarks>
    /// This is the property the whole provider exists for. If the header survived as a fallback —
    /// through a merge, or through an empty answer being read as "nothing to say, use what you have"
    /// — then a caller could name its own roles, and every grant in every definition would be
    /// advisory. <c>204</c> is the exact shape that invites the mistake: it is a successful response
    /// carrying no roles, and treating it as absence rather than as an empty set restores the header.
    /// </remarks>
    [SkippableFact]
    public async Task AnAssertedHeaderRoleDoesNotSurviveAnEmptyProviderAnswer()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.False(
            await IsAuthorizedAsync(Root, chain.RootId, Admin, queryRoles: true, extraHeaders: As(EmptyUser)),
            "the caller named chain.admin in its own headers and morph-idm answered 204 (no " +
            "operations); an allowed verdict means the header was used as a fallback");
    }

    /// <summary>
    /// The same rule on the OTHER channel: the <c>role</c> <b>query parameter</b>. A caller whose
    /// identity service reports no operations must not be able to name its own role in the query
    /// string either.
    /// </summary>
    /// <remarks>
    /// <para>This one was found by probing rather than by reading, after the header case was already
    /// closed and green. `authorize` fell back to the parameter whenever the provider returned an
    /// empty set, with no regard for which provider had returned it. Measured on this lab, same caller
    /// and same instance:</para>
    /// <code>
    /// ?queryRoles=true                  -> {"allowed":false}  403
    /// ?queryRoles=true&amp;role=chain.admin -> {"allowed":true}   200
    /// </code>
    /// <para>It matters more than it looks: since the runtime's own gates were removed, `authorize` is
    /// the only place these questions are answered, so a gateway that forwards the client's query
    /// string would have been admitting on the client's own claim. The fix is a capability on the
    /// resolver (<c>ICallerRoleResolver.AllowsRoleParameterFallback</c>) — false for any provider that
    /// is an authority — rather than a provider-name check inside <c>authorize</c>.</para>
    /// </remarks>
    [SkippableFact]
    public async Task TheRoleQueryParameterDoesNotSurviveAnEmptyProviderAnswer()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.False(
            await IsAuthorizedAsync(Root, chain.RootId, NoRole, queryRoles: true,
                extraHeaders: As(EmptyUser), roleParameter: Admin),
            "morph-idm answered 204 for this caller; naming chain.admin in the query string must not " +
            "override that — it is the header hole reached through a different channel");
    }

    /// <summary>
    /// And the parameter is not merely ignored for <c>queryRoles</c>: <c>ack</c> composes it
    /// ADDITIVELY, which would otherwise leave it as the one target where a caller still names its own
    /// role.
    /// </summary>
    [SkippableFact]
    public async Task TheRoleQueryParameterDoesNotReachTheAckPreflightEither()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        // Nothing is awaiting, so the honest assertion here is agreement with the endpoint rather than
        // a fixed verdict: both answer "allowed" idempotently. What is pinned is that adding the
        // parameter does not CHANGE the answer.
        var withoutParameter = await IsAuthorizedAsync(
            Root, chain.RootId, NoRole, ack: true, extraHeaders: As(EmptyUser));
        var withParameter = await IsAuthorizedAsync(
            Root, chain.RootId, NoRole, ack: true, extraHeaders: As(EmptyUser), roleParameter: Admin);

        Assert.Equal(withoutParameter, withParameter);
    }

    /// <summary>
    /// The other direction: no role header at all, and the identity service supplies the grant. Proves
    /// the answer is actually consumed rather than the header merely being ignored.
    /// </summary>
    [SkippableFact]
    public async Task TheProvidersAnswerGrantsWithNoRoleHeaderAtAll()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.True(
            await IsAuthorizedAsync(Root, chain.RootId, NoRole, queryRoles: true, extraHeaders: As(AdminUser)),
            "morph-idm answers chain.admin for this user and the caller sent no role header; a " +
            "refusal means the provider's answer never reached the grant evaluation");
    }

    /// <summary>
    /// The chain still composes under this provider: a role that clears the root but not the levels
    /// beneath it is refused, exactly as it is under the default provider.
    /// </summary>
    [SkippableFact]
    public async Task TheConjunctionStillHoldsOnProviderSuppliedRoles()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.False(
            await IsAuthorizedAsync(Root, chain.RootId, NoRole, queryRoles: true, extraHeaders: As(LeafOnlyUser)),
            "chain.leaf-only is granted by the leaf and by no level above it");
    }

    /// <summary>
    /// A provider that cannot answer must refuse, not degrade to an empty role set.
    /// </summary>
    /// <remarks>
    /// Empty and unknown look the same one line later and mean opposite things. An unreachable
    /// identity service would, under the permissive reading, turn every allowlist into a silent
    /// blanket denial and every blacklist into a silent blanket ALLOW — the second of which is an
    /// outage that grants access. The refusal is explicit instead: 403 with
    /// <c>Authorization:CallerRoleResolutionFailed</c>.
    /// </remarks>
    [SkippableFact]
    public async Task AnUnreachableProviderRefusesRatherThanResolvingToNoRoles()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        var (status, _) = await AuthorizeAsync(
            Root, chain.RootId, Admin, queryRoles: true, extraHeaders: As(BrokenUser));

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    /// <summary>
    /// The read surfaces resolve caller roles through the SAME provider as <c>authorize</c>. They no
    /// longer refuse anyone, but they still decide what a caller is OFFERED — and that decision has to
    /// be about the same caller the gateway's oracle was asked about.
    /// </summary>
    /// <remarks>
    /// This replaces an earlier assertion that the state function answers <c>403</c> for a caller
    /// morph-idm reports no operations for. That premise is gone with the gate: the read is served
    /// either way. What remains observable — and is the real risk under a provider change — is role
    /// RESOLUTION: if the read path fell back to the <c>role</c> header here while <c>authorize</c>
    /// asked the identity service, the two would be describing different callers and the gateway's
    /// verdict would be attached to the wrong request.
    /// </remarks>
    [SkippableFact]
    public async Task TheReadSurfacesResolveRolesThroughTheSameProvider()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        // morph-idm answers chain.admin for this user, and the caller sends no role header at all.
        var (withRoles, granted) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Root}/instances/{chain.RootId}/functions/state",
            headers: Merge(Headers(NoRole), As(AdminUser)));

        Assert.Equal(HttpStatusCode.OK, withRoles);
        Assert.Contains("record-note", TransitionKeys(Parse(granted)));

        // Same instance, and the caller now ASSERTS chain.admin in its own headers — but morph-idm
        // answers 204 for it. The header must not put the transition back.
        var (withoutRoles, empty) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Root}/instances/{chain.RootId}/functions/state",
            headers: Merge(Headers(Admin), As(EmptyUser)));

        Assert.Equal(HttpStatusCode.OK, withoutRoles);
        Assert.DoesNotContain("record-note", TransitionKeys(Parse(empty)));
    }

    private static IReadOnlyList<string> TransitionKeys(System.Text.Json.JsonElement body)
    {
        var keys = new List<string>();
        if (body.ValueKind != System.Text.Json.JsonValueKind.Object) return keys;
        if (!body.TryGetProperty("transitions", out var transitions)) return keys;

        foreach (var transition in transitions.EnumerateArray())
        {
            if (transition.TryGetProperty("kind", out var kind) && kind.GetString() == "scheduled")
                continue;
            if (transition.TryGetProperty("name", out var name) && name.GetString() is { } key)
                keys.Add(key);
        }

        return keys;
    }

    private static Dictionary<string, string> Merge(
        Dictionary<string, string> baseHeaders, Dictionary<string, string> extra)
    {
        foreach (var (k, v) in extra) baseHeaders[k] = v;
        return baseHeaders;
    }
}
