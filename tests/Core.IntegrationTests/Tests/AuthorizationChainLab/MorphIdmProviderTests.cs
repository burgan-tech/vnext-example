using System.Net;
using System.Text.Json;
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

    /// <summary>
    /// The caller's identity as the gateway forwards it. <c>act_sub</c> is not decoration: since
    /// 2026-09-24 the runtime does not call morph-idm at all for a caller carrying neither
    /// <c>act_sub</c> nor <c>client_id</c> (an anonymous or device token) and resolves it to no roles,
    /// so a <c>sub</c>-only request would never reach the MockLab seed, which is keyed on <c>sub</c>.
    /// </summary>
    private static Dictionary<string, string> As(string user) => new() { ["sub"] = user, ["act_sub"] = user };

    /// <summary>
    /// A caller ASSERTING <c>chain.admin</c> in the <c>role</c> header is evaluated with it, even
    /// though morph-idm would answer "no operations" for this identity — because morph-idm is not asked.
    /// </summary>
    /// <remarks>
    /// Committee decision, 2026-09-25: under morph-idm a request's <c>role</c> header takes precedence
    /// and the identity service is consulted only for a request without one. This replaces the
    /// 2026-09-22 rule that the header decides nothing, which this test used to pin in the opposite
    /// direction (<c>AnAssertedHeaderRoleDoesNotSurviveAnEmptyProviderAnswer</c>).
    /// </remarks>
    [SkippableFact]
    public async Task ARoleHeaderDecides_AndMorphIdmIsNotAsked()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.True(
            await IsAuthorizedAsync(Root, chain.RootId, Admin, queryRoles: true, extraHeaders: As(EmptyUser)),
            "the caller sent role: chain.admin; under the header-precedence rule that is its role set, " +
            "whatever morph-idm would have answered for this identity");
    }

    /// <summary>
    /// The header decides even when morph-idm is broken for this identity: the request never reaches
    /// it, so its failure is not in the picture.
    /// </summary>
    [SkippableFact]
    public async Task ARoleHeaderDecides_EvenWhenMorphIdmWouldFail()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.True(
            await IsAuthorizedAsync(Root, chain.RootId, Admin, queryRoles: true, extraHeaders: As(BrokenUser)),
            "morph-idm answers 500 for this identity, but a request with a role header is not sent to it");
    }

    /// <summary>
    /// The <c>role</c> <b>query parameter</b> behaves like the header under morph-idm: with no role
    /// header, <c>?role=chain.admin</c> is the role set and morph-idm — which would answer "no
    /// operations" for this identity — is not asked.
    /// </summary>
    /// <remarks>
    /// Changed 2026-09-25 (RoleParameterMode.AsRoleHeader). This test used to pin the opposite
    /// (<c>TheRoleQueryParameterDoesNotSurviveAnEmptyProviderAnswer</c>): the parameter was ignored
    /// under morph-idm while the identity service was the only authority. Once the header became
    /// decisive, the same claim answered 200 through the header and 403 through the query string.
    /// </remarks>
    [SkippableFact]
    public async Task TheRoleQueryParameterBehavesLikeTheRoleHeader()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.True(
            await IsAuthorizedAsync(Root, chain.RootId, NoRole, queryRoles: true,
                extraHeaders: As(EmptyUser), roleParameter: Admin),
            "with no role header, ?role=chain.admin is the role set exactly as a role header would be");
    }

    /// <summary>A real role header wins over the parameter.</summary>
    [SkippableFact]
    public async Task ARealRoleHeaderWinsOverTheRoleQueryParameter()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        Assert.False(
            await IsAuthorizedAsync(Root, chain.RootId, "chain.nobody", queryRoles: true,
                extraHeaders: As(AdminUser), roleParameter: Admin),
            "the header says chain.nobody; the parameter must not add chain.admin to it");
    }

    /// <summary>
    /// <c>ack</c> follows the same header rule as the other targets under morph-idm. Nothing is awaiting
    /// here, so both calls answer "allowed" idempotently; what is pinned is that the parameter does not
    /// change that answer.
    /// </summary>
    [SkippableFact]
    public async Task TheRoleQueryParameterDoesNotChangeAnIdleAckAnswer()
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
    /// For a request WITHOUT a role header, a provider that cannot answer resolves to an EMPTY role set —
    /// it no longer breaks the request — and that empty set is refused by an allowlist, not granted.
    /// </summary>
    /// <remarks>
    /// Changed 2026-09-24. The refusal used to be a resolution error (403
    /// <c>Authorization:CallerRoleResolutionFailed</c>) on every surface; now the request is
    /// evaluated on an empty set. The oracle still answers <c>{"allowed":false}</c> because the
    /// root's <c>queryRoles</c> is an allowlist nothing empty can match — a verdict, not an error. The
    /// blacklist half of the old worry is closed in the grant engine: a role-less caller cannot pass a
    /// role-bound deny. The state function is served (200) for the same caller, which is the point.
    /// </remarks>
    [SkippableFact]
    public async Task AnUnreachableProviderIsEvaluatedAsNoRoles_AndTheAllowlistRefusesIt()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var chain = await StartChainAsync();

        // No role header: that is the only request morph-idm is asked about.
        var (status, body) = await AuthorizeAsync(
            Root, chain.RootId, NoRole, queryRoles: true, extraHeaders: As(BrokenUser));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.True(body.TryGetProperty("allowed", out var allowed),
            "the refusal must be the oracle's verdict, not a CallerRoleResolutionFailed error body");
        Assert.False(allowed.GetBoolean());
        Assert.DoesNotContain("110004", body.GetRawText());

        var (stateStatus, _) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Root}/instances/{chain.RootId}/functions/state",
            headers: Merge(Headers(NoRole), As(BrokenUser)));
        Assert.Equal(HttpStatusCode.OK, stateStatus);
    }

    /// <summary>
    /// The read surfaces resolve caller roles through the SAME provider as <c>authorize</c>. They no
    /// longer refuse anyone, but they still decide what a caller is OFFERED — and that decision has to
    /// be about the same caller the gateway's oracle was asked about.
    /// </summary>
    /// <remarks>
    /// What is observable — and is the real risk under a provider change — is role RESOLUTION: the read
    /// path and <c>authorize</c> must describe the same caller. Both apply the same precedence: a role
    /// header when the request carries one, otherwise morph-idm's answer.
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

        // Same instance, an identity morph-idm answers 204 for, and no role header: nothing is offered.
        var (withoutRoles, empty) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Root}/instances/{chain.RootId}/functions/state",
            headers: Merge(Headers(NoRole), As(EmptyUser)));

        Assert.Equal(HttpStatusCode.OK, withoutRoles);
        Assert.DoesNotContain("record-note", TransitionKeys(Parse(empty)));

        // Same identity, now ASSERTING chain.admin in the role header: the header decides (committee
        // decision 2026-09-25), so the read surface offers the transition — the same verdict the
        // oracle gives in ARoleHeaderDecides_AndMorphIdmIsNotAsked.
        var (withHeader, headerBody) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Root}/instances/{chain.RootId}/functions/state",
            headers: Merge(Headers(Admin), As(EmptyUser)));

        Assert.Equal(HttpStatusCode.OK, withHeader);
        Assert.Contains("record-note", TransitionKeys(Parse(headerBody)));
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
