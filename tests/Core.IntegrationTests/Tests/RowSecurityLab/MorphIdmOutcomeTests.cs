using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// The morph-idm run (M): the caller's role set comes from the IDM <c>get-roles</c> mock (MockLab
/// <c>morph-idm-roles-collection.json</c>, keyed on <c>header.sub</c>), not from a <c>role</c> header. Rows A-9, B-7,
/// S-2, S-3, H-2 [pin], H-3, M-1…M-11 (M-8 = B-7), T-7. Single expected column (committee K1).
/// <para>
/// Every caller sends <c>sub</c> AND <c>act_sub</c> (the resolver does not ask the IDM without <c>act_sub</c> or
/// <c>client_id</c>) and no <c>role</c> header, except where a row says otherwise. "Own (BR-001)" is evaluated on
/// latest data: <c>{r1, r2, r5}</c> (R-F2; the plan text's <c>{r1, r2}</c> for M-1 / T-7 predates it).
/// </para>
/// <para>
/// IDM failures (<c>idm-broken</c>, 500) count toward the provider client's consecutive-failure circuit breaker
/// (<c>CircuitBreakerFailureThreshold = 20</c>; one list + seven GETs with one retry = 16 failures). Every class of
/// this assembly runs in the single <c>VNextIntegration</c> collection, so two failure rows in a row would open it;
/// each failure row therefore starts and ends with <see cref="AssertIdmHealthyAsync"/>, a successful IDM answer that
/// resets the count (and fails loudly if the breaker is already open).
/// </para>
/// Runs only when <c>VNEXT_CALLER_ROLE_PROVIDER=morph-idm</c> and the runtime runs with
/// <c>CallerRoleProvider__Provider=morph-idm</c> aimed at MockLab (README § M run).
/// </summary>
public class MorphIdmOutcomeTests : RowSecurityLabTestBase
{
    private const string FlowA = "rs-a";
    private const string FlowAxr = "rs-a-xr";
    private const string FlowB = "rs-b";
    private const string FlowS = "rs-s";
    private const string FlowPlain = "rs-plain";

    private const string MorphIdmOnly =
        "M run: needs VNEXT_CALLER_ROLE_PROVIDER=morph-idm and a runtime on CallerRoleProvider__Provider=morph-idm";

    public MorphIdmOutcomeTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>An IDM-resolved caller: <c>sub</c> = <c>act_sub</c> = <paramref name="user"/>, no role header.</summary>
    private static Caller Idm(string user, string? branch = null, string? role = null) =>
        new(Sub: user, ActSub: user, Role: role, Headers: branch is null ? null : Branch(branch));

    // ── resolution failed ⇒ none ───────────────────────────────────────────

    /// <summary>
    /// A-9 / M-5: <c>idm-broken</c> (IDM 500) + <c>x-branch-code: BR-001</c> on <c>rs-a</c> ⇒ none, GET 404. Resolution
    /// failure is never "no restriction" — not even the branch rule's parameter is consulted.
    /// </summary>
    [SkippableFact]
    public async Task A9_M5_IdmBroken_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertIdmHealthyAsync();
        await AssertVisibleAsync(FlowA, p, Idm("idm-broken", "BR-001"));
        await AssertIdmHealthyAsync();
    }

    /// <summary>
    /// B-7 (= M-8): <c>rs-b</c>, identity alice (<c>act_sub</c>, starter of r1 and r3), IDM 500 ⇒ none. The failed
    /// resolution overrides the <c>$InstanceStarter</c> add-on (a role-less alice would see <c>{r1, r3}</c>, B-2).
    /// </summary>
    [SkippableFact]
    public async Task B7_IdentityWithIdmFailure_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowB);

        await AssertIdmHealthyAsync();
        await AssertVisibleAsync(FlowB, p,
            new Caller(Sub: "idm-broken", ActSub: "alice", Headers: Branch("BR-001")));
        await AssertIdmHealthyAsync();
    }

    /// <summary>
    /// S-2: <c>rs-s</c> (<c>hq-auditor</c> + <c>$InstanceStarter</c>), identity alice, IDM 500 ⇒ none (not alice's
    /// own <c>{r1, r3}</c>).
    /// </summary>
    [SkippableFact]
    public async Task S2_StarterRuleWithIdmFailure_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowS);

        await AssertIdmHealthyAsync();
        await AssertVisibleAsync(FlowS, p, new Caller(Sub: "idm-broken", ActSub: "alice"));
        await AssertIdmHealthyAsync();
    }

    /// <summary>
    /// M-10: <c>rs-plain</c> (no <c>rowSecurity</c>) as <c>idm-broken</c> ⇒ all seven, unchanged: a flow without
    /// row security never depends on the role resolution.
    /// </summary>
    [SkippableFact]
    public async Task M10_FlowWithoutRowSecurity_IdmBroken_SeesAll()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowPlain);

        await AssertIdmHealthyAsync();
        await AssertVisibleAsync(FlowPlain, p, Idm("idm-broken", "BR-001"), [.. p.All]);
        await AssertIdmHealthyAsync();
    }

    /// <summary>M-6: <c>rs-idm-garbage</c> (200, unparseable body) ⇒ none.</summary>
    [SkippableFact]
    public async Task M6_IdmGarbageBody_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-garbage", "BR-001"));
    }

    /// <summary>
    /// M-7 [pin]: the IDM timeout row cannot be staged. MockLab applies <c>delayMs</c> per mock, not per rule, so no
    /// <c>rs-idm-slow</c> rule exists (Task 11A gap, controller ruling). Today <c>rs-idm-slow</c> is an unkeyed user:
    /// MockLab answers at once with its default operation set (no lab role) and the row reads none. Pins that the
    /// timeout path is NOT exercised end to end (it is unit-only); turns red once a delayed rule is seeded — then
    /// replace this pin with the real M-7 row (expected none) in an isolated last collection (the timeouts would
    /// count toward the circuit breaker).
    /// </summary>
    [SkippableFact]
    public async Task M7_Pinned_IdmTimeoutNotSeedable_SlowUserGetsDefaultAnswer()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);

        var mockLab = System.Environment.GetEnvironmentVariable("MOCKLAB_BASE_URL") is { Length: > 0 } url
            ? url
            : "http://localhost:3001";
        using var http = new HttpClient { BaseAddress = new Uri(mockLab.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/1/morph-idm/functions/get-roles");
        request.Headers.TryAddWithoutValidation("sub", "rs-idm-slow");
        request.Headers.TryAddWithoutValidation("act_sub", "rs-idm-slow");
        var clock = Stopwatch.StartNew();
        using var response = await http.SendAsync(request);
        clock.Stop();
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"MockLab get-roles for rs-idm-slow answered {(int)response.StatusCode}: {body}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
            $"rs-idm-slow answered after {clock.Elapsed.TotalSeconds:F1}s — a delayed rule exists now; replace this pin with the real M-7 row");
        Assert.DoesNotContain("branch-staff", body);
        Assert.DoesNotContain(HqAuditor, body);

        var p = await SeedAsync(FlowA);
        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-slow", "BR-001"));
    }

    // ── IDM answers ─────────────────────────────────────────────────────────

    /// <summary>M-1: <c>rs-idm-branch</c> (IDM ⇒ <c>[branch-staff]</c>) + BR-001 ⇒ own branch <c>{r1, r2, r5}</c>.</summary>
    [SkippableFact]
    public async Task M1_IdmBranchStaff_SeesOwnBranch()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-branch", "BR-001"), p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// M-2: <c>rs-idm-hq</c> (<c>[hq-auditor]</c>) and <c>rs-idm-both</c> (<c>[branch-staff, hq-auditor]</c>, OR of
    /// the rules: the unfiltered one wins) ⇒ all seven.
    /// </summary>
    [SkippableFact]
    public async Task M2_IdmHqAuditorAndBoth_SeeAll()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-hq"), [.. p.All]);
        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-both", "BR-001"), [.. p.All]);
    }

    /// <summary>M-3: <c>rs-idm-menu</c> (<c>[menu.view]</c>, no rule) + BR-001 ⇒ none (K1: no static match).</summary>
    [SkippableFact]
    public async Task M3_IdmUnrelatedRole_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-menu", "BR-001"));
    }

    /// <summary>
    /// M-4: an empty answer is role-less, not a failure — <c>idm-empty</c> (204), <c>rs-idm-blank</c> (200, blank
    /// body), <c>rs-idm-emptyroles</c> (<c>{"roles":[]}</c>) + BR-001 ⇒ none each.
    /// </summary>
    [SkippableFact]
    public async Task M4_IdmEmptyAnswers_SeeNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        foreach (var user in new[] { "idm-empty", "rs-idm-blank", "rs-idm-emptyroles" })
            await AssertVisibleAsync(FlowA, p, Idm(user, "BR-001"));
    }

    /// <summary>
    /// M-9: no <c>act_sub</c> / <c>client_id</c> ⇒ the IDM is not asked (role-less) ⇒ none on <c>rs-a</c>, even with
    /// <c>sub: rs-idm-hq</c> (which the IDM would answer with <c>[hq-auditor]</c>).
    /// </summary>
    [SkippableFact]
    public async Task M9_NoActSubNoClientId_IdmSkipped_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, new Caller(Sub: "rs-idm-hq", Headers: Branch("BR-001")));
    }

    /// <summary>
    /// S-3: the same skipped caller on <c>rs-s</c> ⇒ none: no role, and no <c>act_sub</c> for <c>$InstanceStarter</c>
    /// to bind (no identity value ⇒ the identity rule contributes nothing).
    /// </summary>
    [SkippableFact]
    public async Task S3_NoActSubNoClientId_StarterRule_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowS);

        await AssertVisibleAsync(FlowS, p, new Caller(Sub: "rs-idm-hq"));
        await AssertVisibleAsync(FlowS, p, new Caller(Sub: "alice"));
    }

    /// <summary>
    /// M-11: an opted-in flow whose schema also carries <c>x-roles</c> (<c>rs-a-xr</c>): two role-dependent surfaces
    /// on one request. <c>rs-idm-branch</c> + BR-001 ⇒ own branch <c>{r1, r2, r5}</c> on list and GET, and the GET
    /// body is the instance (masking ran on the same resolved set). "Exactly one non-memo <c>Auth.ResolveRoles</c>
    /// span per request" is asserted from traces in Task 12.
    /// </summary>
    [SkippableFact]
    public async Task M11_RowSecurityWithXRoles_SameResolvedSet()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowAxr);
        var caller = Idm("rs-idm-branch", "BR-001");

        await AssertVisibleAsync(FlowAxr, p, caller, p.R1, p.R2, p.R5);

        var (status, body, _) = await GetAsync(FlowAxr, p.R1.ToString(), caller);
        Assert.Equal(HttpStatusCode.OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(p.R1.ToString(), root.GetProperty("id").GetString(), ignoreCase: true);
        var attributes = root.GetProperty("attributes");
        Assert.Equal(p.TestId, attributes.GetProperty("testId").GetString());
        Assert.Equal("BR-001", attributes.GetProperty("branchCode").GetString());
    }

    // ── client role header under morph-idm ──────────────────────────────────

    /// <summary>
    /// H-2 [pin]: <c>rs-idm-branch</c> + BR-001 + client-sent <c>role: hq-auditor</c> ⇒ all seven. A non-empty role
    /// header replaces the IDM answer (the IDM is not asked); the gateway must strip or overwrite it
    /// (docs/domain/row-security.md § Gateway precondition). Asserts today's behaviour.
    /// </summary>
    [SkippableFact]
    public async Task H2_Pinned_ClientRoleHeaderReplacesIdmAnswer_SeesAll()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-branch", "BR-001", role: HqAuditor), [.. p.All]);
    }

    /// <summary>
    /// H-3: <c>rs-idm-branch</c> + BR-001 + client-sent <c>role: none</c> ⇒ none. The header replaces the IDM's
    /// <c>[branch-staff]</c> with an unmatched role; under K1 that is deny (the IDM alone would give
    /// <c>{r1, r2, r5}</c>, M-1).
    /// </summary>
    [SkippableFact]
    public async Task H3_ClientUnmatchedRoleHeader_SeesNothing()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);

        await AssertVisibleAsync(FlowA, p, Idm("rs-idm-branch", "BR-001", role: "none"));
    }

    // ── trigger task with another credential ───────────────────────────────

    /// <summary>
    /// T-7: caller <c>rs-idm-hq</c> (IDM ⇒ <c>[hq-auditor]</c>) runs <c>rs-reader</c>; the task mapping sets
    /// <c>sub</c> = <c>act_sub</c> = <c>rs-idm-branch</c> + <c>x-branch-code: BR-001</c> ⇒ the read is made as
    /// <c>rs-idm-branch</c>: own branch <c>{r1, r2, r5}</c> through GetInstances and GetInstance. Seeing all seven
    /// would mean the morph-idm memo served the caller's set for the mapped credential.
    /// </summary>
    [SkippableFact]
    public async Task T7_TaskMappedCredential_GetsItsOwnIdmAnswer()
    {
        Skip.IfNot(ProviderIs("morph-idm"), MorphIdmOnly);
        var p = await SeedAsync(FlowA);
        var caller = Idm("rs-idm-hq");
        var mapped = new Dictionary<string, string>
        {
            ["sub"] = "rs-idm-branch",
            ["act_sub"] = "rs-idm-branch",
            ["x-branch-code"] = "BR-001",
        };

        // Control: the caller itself is an hq-auditor through the IDM.
        await AssertVisibleAsync(FlowA, p, caller, [.. p.All]);

        var expected = new[] { p.R1, p.R2, p.R5 };
        var list = await ReadThroughReaderAsync(caller, "read-list",
            new { targetFlow = FlowA, testId = p.TestId, headers = mapped });
        Assert.True(list.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"GetInstances task result: {list}");
        var ids = list.GetProperty("ids").EnumerateArray().Select(id => Guid.Parse(id.GetString()!)).ToList();
        Assert.True(expected.ToHashSet().SetEquals(ids),
            $"GetInstances {FlowA}: expected [{Names(p, expected)}], got [{Names(p, ids)}]");

        foreach (var id in p.All)
        {
            var get = await ReadThroughReaderAsync(caller, "read-one",
                new { targetFlow = FlowA, targetId = id.ToString(), headers = mapped });
            var want = expected.Contains(id) ? 200 : 404;
            var got = get.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : (int?)null;
            Assert.True(got == want, $"GetInstance {FlowA}/{Names(p, [id])} recorded {got}, expected {want}: {get}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One successful IDM resolution (<c>rs-idm-hq</c> lists <c>rs-a</c>, page size 1, no testId scope) and asserts it
    /// sees a row: resets the provider client's consecutive-failure count, and fails loudly if the circuit breaker is
    /// already open (the hq caller would then read none).
    /// </summary>
    private async Task AssertIdmHealthyAsync()
    {
        var (status, body) = await ListRawAsync(FlowA, Idm("rs-idm-hq"), RawNoTestId, pageSize: 1);
        Assert.True(status == HttpStatusCode.OK, $"IDM health probe (rs-idm-hq list rs-a) answered {(int)status}: {body}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("items").GetArrayLength() == 1,
            $"IDM health probe: rs-idm-hq saw no rs-a row — is the morph-idm circuit breaker open? {body}");
    }
}
