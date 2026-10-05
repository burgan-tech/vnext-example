using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Role-grant combinators (<c>allOf</c> / <c>anyOf</c>) on three surfaces, against the
/// <c>role-matrix-lab-combinators</c> flow: transition four-eyes through
/// <c>authorize?transitionKey=</c>, a state's <c>queryRoles</c> through <c>authorize?queryRoles=true</c>,
/// and schema <c>x-roles</c> through the <c>data</c> function.
/// </summary>
/// <remarks>
/// <para><b>Evaluation model</b> (vnext <c>feature/role-grant-combinators</c>, docs/domain/role-grant-authorization.md
/// § Combinators). A role-bound leaf (static role) is Unknown for a caller with no roles; an identity
/// leaf (<c>$InstanceStarter</c>, <c>$PreviousUser</c>, <c>$InstanceBehalfOfStarter</c>, …) is always
/// Yes/No. <c>allOf</c>: No dominates, else Unknown, else Yes. <c>anyOf</c>: Yes dominates, else
/// Unknown, else No. A DENY fires on Yes or Unknown; an ALLOW admits only on Yes.</para>
/// <para><b>Identity.</b> <c>$InstanceStarter</c> / <c>$PreviousUser</c> compare the caller's
/// <c>act_sub</c> with the instance's <c>CreatedBy</c> / the last manual transition's <c>CreatedBy</c>;
/// the behalf-of pair compares <c>sub</c> with <c>CreatedByBehalfOf</c>. Every request here therefore
/// sends both headers explicitly (<see cref="As"/>).</para>
/// <para>The cast mirrors the plan's illustration (§ "Yüzey gösterimleri"): the case is started by
/// ALİ (<c>act_sub=u-ali</c>) on behalf of the corporate subject <c>c-acme</c> (<c>sub</c>), and is
/// about the customer VELİ (<c>customerId=u-veli</c>).</para>
/// </remarks>
public sealed class CombinatorGrantTests : RoleMatrixLabTestBase
{
    public CombinatorGrantTests(VNextTestEnvironment environment) : base(environment) { }

    private const string Flow = "role-matrix-lab-combinators";

    private const string Customer = "morph-idm.customer";
    private const string CorporateOps = "morph-idm.corporate-ops";

    private const string Iban = "TR330006100519786457841326";
    private const string RiskNote = "watch-list: manual review";

    /// <summary>A caller: role header (or none), actor (<c>act_sub</c>) and subject (<c>sub</c>).</summary>
    private sealed record Caller(string? Roles, string ActSub, string Sub);

    // The plan's cast.
    private static readonly Caller Ali = new(Customer, "u-ali", "c-acme");            // starter: act_sub u-ali, acting for c-acme (sub)
    private static readonly Caller Ops = new(CorporateOps, "u-ops", "c-acme");        // behalf-of starter (sub)
    private static readonly Caller OpsElsewhere = new(CorporateOps, "u-ops", "c-other");
    private static readonly Caller Veli = new(null, "u-veli", "c-acme");              // role-less, sub = behalf-of starter
    private static readonly Caller Anon = new(null, "u-x", "u-x");

    /// <summary>Headers for <paramref name="caller"/>: the role headers plus <c>sub</c> and <c>act_sub</c>.</summary>
    private static Dictionary<string, string> As(Caller caller)
    {
        var headers = HeadersFor(caller.Roles);
        headers["act_sub"] = caller.ActSub;
        headers["sub"] = caller.Sub;
        return headers;
    }

    /// <summary>
    /// Starts a case AS ALİ on behalf of <c>c-acme</c>: <c>CreatedBy = u-ali</c> (from <c>act_sub</c>),
    /// <c>CreatedByBehalfOf = c-acme</c> (from <c>sub</c> — which is why <see cref="Ali"/>'s subject is
    /// <c>c-acme</c>, not <c>u-ali</c>). The start body becomes the instance data.
    /// </summary>
    private async Task<string> StartCaseAsAliAsync(string tag)
    {
        var response = await Api.StartInstanceAsync(Flow, new
        {
            caseRef = $"{tag}-{Guid.NewGuid():N}"[..24],
            customerId = "u-veli",
            iban = Iban,
            riskNote = RiskNote
        }, As(Ali));

        var instanceId = response.Body.GetProperty("id").GetString()
                         ?? throw new InvalidOperationException("start response carried no instance id");

        await WaitUntilSettledAsync(Flow, instanceId, Ali.Roles);
        await AssertNotFaultedAsync(Flow, instanceId, Ali.Roles);
        return instanceId;
    }

    private async Task<HttpStatusCode> SendAsAsync(Caller caller, HttpMethod method, string url, object? body = null)
    {
        var (status, _) = await SendRawAsync(method, url, body, As(caller));
        return status;
    }

    /// <summary><c>authorize</c>'s verdict for <paramref name="caller"/>; 200 → true, 403 → false, else fail.</summary>
    private async Task<bool> IsAuthorizedAsAsync(Caller caller, string instanceId, string query)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Flow}/instances/{instanceId}/functions/authorize?{query}",
            headers: As(caller));

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"authorize answered {(int)status}: {body}");
        return status == HttpStatusCode.OK;
    }

    /// <summary>The <c>data</c> function's attributes for <paramref name="caller"/>.</summary>
    private async Task<JsonElement> DataAsAsync(Caller caller, string instanceId)
    {
        var headers = As(caller);
        // Read fresh: the data function's response cache is keyed by CallerScopeHash, which since this
        // branch includes `sub`. OPS and OPS-elsewhere differ ONLY by sub — a shared entry would be
        // exactly the leak the subject-in-scope-hash rule closes, so the cached path is exercised separately below.
        headers["X-VNext-Cache-Override"] = "true";

        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{Flow}/instances/{instanceId}/functions/data", headers: headers);
        Assert.True(status == HttpStatusCode.OK, $"data function answered {(int)status}: {body}");

        var root = JsonDocument.Parse(body).RootElement.Clone();
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
    }

    private static string? Str(JsonElement attributes, string name) =>
        attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ── §2 transition four-eyes: allow maker, deny allOf[maker, $PreviousUser] ──

    /// <summary>
    /// AYŞE (maker) submits the case and becomes <c>$PreviousUser</c>; she may not approve it. Another
    /// maker may. ALİ — the starter, not the previous user — may, once he holds the maker role, which
    /// separates <c>$PreviousUser</c> from <c>$InstanceStarter</c>. A role-less caller is refused by the
    /// allow side only: the deny's <c>allOf</c> is <c>Unknown ∧ No = No</c> and does not fire.
    /// </summary>
    [Fact]
    public async Task FourEyes_TheMakerWhoSubmittedMayNotApprove_AnotherMakerMay()
    {
        var instanceId = await StartCaseAsAliAsync("comb-4eyes");

        var ayse = new Caller(Maker, "u-ayse", "u-ayse");
        var submit = await SendAsAsync(ayse, HttpMethod.Patch,
            $"api/v1/core/workflows/{Flow}/instances/{instanceId}/transitions/submit?sync=false", new { });
        Assert.True((int)submit < 400, $"submit was refused with {(int)submit}");
        await WaitForInstanceStateAsync(Flow, instanceId, "checking", Maker);

        Assert.False(await IsAuthorizedAsAsync(ayse, instanceId, "transitionKey=approve"),
            "AYŞE holds maker AND made the previous manual transition: deny allOf = Yes ∧ Yes fires");

        Assert.True(await IsAuthorizedAsAsync(new Caller(Maker, "u-mehmet", "u-mehmet"), instanceId, "transitionKey=approve"),
            "another maker: deny allOf = Yes ∧ No = No, allow maker = Yes");

        Assert.True(await IsAuthorizedAsAsync(new Caller(Maker, "u-ali", "c-acme"), instanceId, "transitionKey=approve"),
            "the STARTER is not the previous user; a refusal means $PreviousUser resolved to the starter");

        Assert.False(await IsAuthorizedAsAsync(new Caller(null, "u-x", "u-x"), instanceId, "transitionKey=approve"),
            "role-less: allow maker is Unknown and admits nothing");

        Assert.False(await IsAuthorizedAsAsync(new Caller(Approver, "u-mehmet", "u-mehmet"), instanceId, "transitionKey=approve"),
            "a non-maker is refused by the allow side");
    }

    // ── §2 queryRoles: allow allOf[customer, $InstanceStarter] ──

    /// <summary>
    /// Visibility of <c>draft</c> requires BOTH the customer role and being the starter. Each half
    /// alone is refused, and a role-less starter is refused because the role leaf is Unknown and an
    /// allow admits only on Yes.
    /// </summary>
    [Fact]
    public async Task QueryRoles_AllOfCustomerAndStarter_AdmitsOnlyTheStarterWhoIsACustomer()
    {
        var instanceId = await StartCaseAsAliAsync("comb-query");

        Assert.True(await IsAuthorizedAsAsync(Ali, instanceId, "queryRoles=true"),
            "ALİ: customer = Yes, $InstanceStarter (act_sub u-ali = CreatedBy) = Yes");

        Assert.False(await IsAuthorizedAsAsync(new Caller(Customer, "u-other", "u-other"), instanceId, "queryRoles=true"),
            "a customer who did not start the case: Yes ∧ No = No");

        Assert.False(await IsAuthorizedAsAsync(new Caller(Maker, "u-ali", "u-ali"), instanceId, "queryRoles=true"),
            "the starter without the customer role: No ∧ Yes = No");

        Assert.False(await IsAuthorizedAsAsync(new Caller(null, "u-ali", "u-ali"), instanceId, "queryRoles=true"),
            "the starter with no roles: Unknown ∧ Yes = Unknown, and an allow admits only on Yes");
    }

    // ── §3 x-roles combinators, observed through the data function ──

    /// <summary>
    /// <c>iban</c>: <c>allow anyOf[$InstanceStarter, $InstanceBehalfOfStarter]</c> + <c>allow corporate-ops</c>,
    /// masked (<c>keepLast: 4</c>) for everyone but corporate-ops. <c>riskNote</c>: <c>allow corporate-ops</c>
    /// + <c>deny allOf[corporate-ops, $InstanceBehalfOfStarter]</c>.
    /// </summary>
    /// <remarks>
    /// The plan's table (§3), row by row:
    /// <list type="table">
    ///   <item>ALİ (starter, sub c-acme): iban visible (anyOf Yes ∨ Yes), masked; riskNote absent (allow ops = No; deny No ∧ Yes = No).</item>
    ///   <item>OPS (sub = behalf-of starter): iban visible and RAW (exemption); riskNote absent (deny Yes ∧ Yes).</item>
    ///   <item>ops for another subject: iban raw; riskNote PRESENT (deny Yes ∧ No = No, allow Yes).</item>
    ///   <item>VELİ (role-less, sub = behalf-of starter): iban visible (anyOf No ∨ Yes), masked; riskNote absent (allow Unknown).</item>
    ///   <item>ANON: iban absent (anyOf No, ops Unknown); riskNote absent.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task XRoles_Combinators_PruneAndMaskPerCaller()
    {
        var instanceId = await StartCaseAsAliAsync("comb-xroles");

        var ali = await DataAsAsync(Ali, instanceId);
        AssertMasked(Str(ali, "iban"), "ALİ is the starter (and the behalf-of starter): anyOf admits, but he is not in the masking exemption");
        Assert.Null(Str(ali, "riskNote"));
        Assert.Equal("u-veli", Str(ali, "customerId"));

        var ops = await DataAsAsync(Ops, instanceId);
        Assert.Equal(Iban, Str(ops, "iban"));
        Assert.True(Str(ops, "riskNote") is null,
            "OPS acts for c-acme, the behalf-of starter: deny allOf[ops, $InstanceBehalfOfStarter] = Yes ∧ Yes fires");

        var opsElsewhere = await DataAsAsync(OpsElsewhere, instanceId);
        Assert.Equal(Iban, Str(opsElsewhere, "iban"));
        Assert.Equal(RiskNote, Str(opsElsewhere, "riskNote"));

        var veli = await DataAsAsync(Veli, instanceId);
        AssertMasked(Str(veli, "iban"),
            "VELİ has no roles but his sub is the behalf-of starter: anyOf No ∨ Yes = Yes");
        Assert.Null(Str(veli, "riskNote"));

        var anon = await DataAsAsync(Anon, instanceId);
        Assert.Null(Str(anon, "iban"));
        Assert.Null(Str(anon, "riskNote"));
        Assert.Equal("u-veli", Str(anon, "customerId"));
    }

    /// <summary>
    /// The cached data path must not serve one subject's pruning to another. OPS and OPS-elsewhere
    /// differ only by <c>sub</c>; since this branch <c>CallerScopeHash</c> includes it (subject-in-scope-hash rule). Without the
    /// cache override header the second read would be served from the first one's entry if the key
    /// still ignored <c>sub</c>.
    /// </summary>
    [Fact]
    public async Task XRoles_TheCachedDataPathKeysOnTheSubject()
    {
        var instanceId = await StartCaseAsAliAsync("comb-cache");

        async Task<string> CachedReadAsync(Caller caller)
        {
            var (status, body) = await SendRawAsync(HttpMethod.Get,
                $"api/v1/core/workflows/{Flow}/instances/{instanceId}/functions/data", headers: As(caller));
            Assert.Equal(HttpStatusCode.OK, status);
            return body;
        }

        Assert.DoesNotContain(RiskNote, await CachedReadAsync(Ops));
        Assert.Contains(RiskNote, await CachedReadAsync(OpsElsewhere));
        Assert.DoesNotContain(RiskNote, await CachedReadAsync(Ops));
    }

    private static void AssertMasked(string? value, string because)
    {
        Assert.True(value is not null, $"the field was pruned, expected it masked — {because}");
        Assert.NotEqual(Iban, value);
        Assert.EndsWith(Iban[^4..], value!);
    }
}
