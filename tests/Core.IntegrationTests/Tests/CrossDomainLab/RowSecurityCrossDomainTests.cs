using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.RowSecurityLab;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// Row security across a domain boundary (vnext#1107, rows X-1…X-6 and T-9x): <c>core</c>'s <c>rs-reader</c> reads the
/// <c>partner</c> flow <c>rs-xd-target</c> (Definition A: <c>branch-staff → branchCode eq x-branch-code</c>,
/// <c>hq-auditor</c> unfiltered) through the GetInstances (15), GetInstance (19) and DirectTrigger (12) tasks with
/// <c>useDapr: true</c>. The partner runtime applies ITS OWN rule set to the header set the calling runtime presents.
/// <para>
/// A trigger task forwards only the credential (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>,
/// <c>role</c>); a rule header such as <c>x-branch-code</c> reaches the partner only when the task mapping sets it
/// (X-1 mapped vs X-2 unmapped). "Own (BR-001)" is evaluated on latest data: <c>{r1, r2, r5}</c> (R-F2).
/// </para>
/// <para>
/// Needs the cross-domain lab (<c>labs/cross-domain/lab.sh up</c>) with <c>VNEXT_PARTNER_BASE_URL</c>; without it
/// every row skips. Default caller-role provider only (the role header is the role set on both sides).
/// </para>
/// </summary>
[Collection("VNextIntegration")]
public class RowSecurityCrossDomainTests : RowSecurityLabTestBase, IClassFixture<CrossDomainLabFixture>
{
    private const string PartnerDomain = "partner";
    private const string Target = "rs-xd-target";

    private readonly CrossDomainLabFixture _lab;
    private readonly HttpClient? _partner;

    public RowSecurityCrossDomainTests(VNextTestEnvironment environment, CrossDomainLabFixture lab) : base(environment)
    {
        _lab = lab;
        if (lab.PartnerBaseUrl is not null)
            _partner = new HttpClient { BaseAddress = new Uri(lab.PartnerBaseUrl.TrimEnd('/') + "/") };
    }

    private void RequireLab()
    {
        Skip.If(_lab.PartnerBaseUrl is null,
            "VNEXT_PARTNER_BASE_URL is not set or not up — the cross-domain lab (labs/cross-domain/lab.sh up) is required.");
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
    }

    private static Caller BranchStaff(string branch) =>
        new(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch(branch));

    private static Caller Roleless() => new(Sub: "alice", ActSub: "alice");

    /// <summary>X-1: <c>branch-staff</c> + <c>x-branch-code: BR-001</c>, the mapping sets the rule header ⇒ own branch.</summary>
    [SkippableFact]
    public async Task X1_RemoteRead_RuleHeaderMapped_SeesOwnBranch()
    {
        RequireLab();
        var p = await SeedPartnerAsync();

        await AssertRemoteVisibleAsync(p, BranchStaff("BR-001"), Branch("BR-001"), p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// X-2: the same caller with no mapping ⇒ none. The role travels, <c>x-branch-code</c> does not, so the partner's
    /// branch rule has no parameter and contributes nothing.
    /// </summary>
    [SkippableFact]
    public async Task X2_RemoteRead_RuleHeaderUnmapped_SeesNothing()
    {
        RequireLab();
        var p = await SeedPartnerAsync();

        await AssertRemoteVisibleAsync(p, BranchStaff("BR-001"), mapped: null);
    }

    /// <summary>X-3: <c>hq-auditor</c>, no mapping ⇒ all seven (the forwarded role opens the partner's unfiltered rule).</summary>
    [SkippableFact]
    public async Task X3_RemoteRead_HqAuditorForwarded_SeesAll()
    {
        RequireLab();
        var p = await SeedPartnerAsync();

        await AssertRemoteVisibleAsync(p, Hq(), mapped: null, [.. p.All]);
    }

    /// <summary>
    /// X-4: remote GetInstance of an excluded id (r3, BR-002, for <c>branch-staff</c> BR-001 mapped) records the same
    /// 404 — status, success flag and error text with the id masked — as a nonexistent id.
    /// </summary>
    [SkippableFact]
    public async Task X4_RemoteGetOfExcludedRow_EqualsMissing()
    {
        RequireLab();
        var p = await SeedPartnerAsync();
        var caller = BranchStaff("BR-001");

        var excluded = await RemoteGetAsync(caller, p.R3.ToString(), Branch("BR-001"));
        var missing = await RemoteGetAsync(caller, Guid.NewGuid().ToString(), Branch("BR-001"));

        Assert.Equal(404, StatusCode(excluded));
        Assert.Equal(StatusCode(missing), StatusCode(excluded));
        Assert.Equal(missing.GetProperty("isSuccess").ToString(), excluded.GetProperty("isSuccess").ToString());
        Assert.NotNull(ErrorShape(excluded));
        Assert.Equal(ErrorShape(missing), ErrorShape(excluded));

        // Control: the same caller and mapping does see r1 through the same task.
        Assert.Equal(200, StatusCode(await RemoteGetAsync(caller, p.R1.ToString(), Branch("BR-001"))));
    }

    /// <summary>
    /// X-5: a role-less caller sees nothing on the partner, and the bypass attempts change nothing: a mapped
    /// <c>x-system-read: true</c> header on the task, and directly on the partner <c>?systemRead=true</c> plus the same
    /// header. No DTO carries a bypass member, so both must answer exactly like the plain role-less read.
    /// </summary>
    [SkippableFact]
    public async Task X5_Roleless_BypassAttempts_SeeNothing()
    {
        RequireLab();
        var p = await SeedPartnerAsync();
        var bypass = new Dictionary<string, string> { ["x-system-read"] = "true" };

        // Through the task: plain, and with the bypass header mapped.
        await AssertRemoteVisibleAsync(p, Roleless(), mapped: null);
        await AssertRemoteVisibleAsync(p, Roleless(), bypass);

        // Directly on the partner: query flag + header, list and every GET. The query bypass is exercised here because a
        // trigger task forwards only the credential headers — a task has no query-parameter counterpart.
        var direct = Roleless() with { Headers = bypass, Query = "systemRead=true" };
        var (status, body) = await PartnerListAsync(p, direct);
        Assert.True(status == HttpStatusCode.OK, $"partner list with bypass attempt answered {(int)status}: {body}");
        Assert.Empty(Ids(body));
        foreach (var id in p.All)
        {
            var (getStatus, getBody) = await PartnerGetAsync(id.ToString(), direct);
            Assert.True(getStatus == HttpStatusCode.NotFound,
                $"partner GET {Names(p, [id])} with bypass attempt answered {(int)getStatus}: {getBody}");
        }

        // Control: the same direct read as hq-auditor sees all seven.
        var (hqStatus, hqBody) = await PartnerListAsync(p, Hq());
        Assert.True(hqStatus == HttpStatusCode.OK, $"partner list as hq answered {(int)hqStatus}: {hqBody}");
        Assert.True(p.All.ToHashSet().SetEquals(Ids(hqBody)), $"partner list as hq: {hqBody}");
    }

    /// <summary>
    /// X-6 [pin, known gap by design]: the task mapping sets <c>sub</c> = <c>act_sub</c> = carol for a caller alice, and
    /// the partner records the transition as carol — the target trusts the identity the calling runtime presents
    /// (doc § Cross-domain tasks: "as strong as the mapping's author"). Pins today's behaviour.
    /// </summary>
    [SkippableFact]
    public async Task X6_Pinned_MappedIdentity_ReadsAsCarol()
    {
        RequireLab();
        var p = await SeedPartnerAsync();
        var mapped = new Dictionary<string, string> { ["sub"] = "carol", ["act_sub"] = "carol", ["role"] = HqAuditor };

        var result = await ReadThroughReaderAsync(new Caller(Sub: "alice", ActSub: "alice", Role: HqAuditor), "trigger-one",
            new { targetFlow = Target, targetDomain = PartnerDomain, useDapr = "true", targetId = p.R6.ToString(),
                  transitionName = "rs-update", headers = mapped });
        Assert.True(result.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"mapped trigger failed: {result}");

        var actor = await LastTransitionActorAsync(p.R6, "rs-update");
        Assert.Equal("carol", actor);
    }

    /// <summary>
    /// T-9x [pin, contested contract routed to the user]: a role-less caller DirectTriggers <c>rs-update</c> into the partner by KEY. The remote path never uses the
    /// caller's local id-only port: the key travels in the URL and the PARTNER resolves it. Measured 2026-10-26 on the
    /// feature runtime: the partner resolves the key as a runtime-internal lookup (doc § Surfaces: "direct-trigger key
    /// resolution … not caller reads and are not filtered"), so the trigger SUCCEEDS and the target is updated, although
    /// the same role-less caller gets 404 for that key on the partner's list and GET. Pins that behaviour: the
    /// transition surface is not row-gated in phase 1 on either side of the boundary.
    /// Side finding pinned with it: the successful trigger's response returns the target's full <c>attributes</c> to
    /// that role-less caller.
    /// </summary>
    [SkippableFact]
    public async Task T9x_Pinned_RolelessRemoteDirectTriggerByKey_Succeeds()
    {
        RequireLab();
        var p = await SeedPartnerAsync();
        var key = $"rs-t9x-{Guid.NewGuid():N}"[..24];
        var keyed = await StartPartnerRowAsync(p.TestId + "-k", "alice", null, "BR-002", 1, "south", "alice", "POS-A", key);

        // Precondition: the role-less caller cannot read the row on the partner, by key or by id.
        var (byKeyStatus, byKeyBody) = await PartnerGetAsync(key, Roleless());
        Assert.True(byKeyStatus == HttpStatusCode.NotFound, $"role-less partner GET by key answered {(int)byKeyStatus}: {byKeyBody}");
        var (byIdStatus, byIdBody) = await PartnerGetAsync(keyed.ToString(), Roleless());
        Assert.True(byIdStatus == HttpStatusCode.NotFound, $"role-less partner GET by id answered {(int)byIdStatus}: {byIdBody}");

        var roleless = await ReadThroughReaderAsync(Roleless(), "trigger-one",
            new { targetFlow = Target, targetDomain = PartnerDomain, useDapr = "true", targetKey = key, transitionName = "rs-update" });
        Assert.True(roleless.GetProperty("isSuccess").ValueKind == JsonValueKind.True,
            $"role-less remote DirectTrigger by key did not succeed: {roleless}");
        var status = StatusCode(roleless);
        Assert.True(status is >= 200 and < 300, $"role-less remote DirectTrigger by key recorded {status}: {roleless}");
        await AssertPartnerTouchedAsync(keyed, expectTouched: true);
    }

    // ── remote reads through rs-reader ───────────────────────────────────────

    /// <summary>
    /// One remote row: GetInstances (scoped to the population's testId) lists exactly <paramref name="expected"/>, and
    /// GetInstance records 200 for each expected id and 404 for every other population id — both with the same caller
    /// and the same <paramref name="mapped"/> headers (null = no mapping).
    /// </summary>
    private async Task AssertRemoteVisibleAsync(
        Population p, Caller caller, IDictionary<string, string>? mapped, params Guid[] expected)
    {
        object listBody = mapped is null
            ? new { targetFlow = Target, targetDomain = PartnerDomain, useDapr = "true", testId = p.TestId }
            : new { targetFlow = Target, targetDomain = PartnerDomain, useDapr = "true", testId = p.TestId, headers = mapped };
        var list = await ReadThroughReaderAsync(caller, "read-list", listBody);
        Assert.True(list.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"remote GetInstances result: {list}");
        var ids = list.GetProperty("ids").EnumerateArray().Select(id => Guid.Parse(id.GetString()!)).ToList();
        Assert.True(expected.ToHashSet().SetEquals(ids),
            $"remote GetInstances {Target}: expected [{Names(p, expected)}], got [{Names(p, ids)}]");

        foreach (var id in p.All)
        {
            var get = await RemoteGetAsync(caller, id.ToString(), mapped);
            var want = expected.Contains(id) ? 200 : 404;
            Assert.True(StatusCode(get) == want,
                $"remote GetInstance {Target}/{Names(p, [id])} recorded {StatusCode(get)}, expected {want}: {get}");
        }
    }

    private Task<JsonElement> RemoteGetAsync(Caller caller, string targetId, IDictionary<string, string>? mapped) =>
        ReadThroughReaderAsync(caller, "read-one", mapped is null
            ? new { targetFlow = Target, targetDomain = PartnerDomain, useDapr = "true", targetId }
            : new { targetFlow = Target, targetDomain = PartnerDomain, useDapr = "true", targetId, headers = mapped });

    private static int? StatusCode(JsonElement result) =>
        result.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : null;

    /// <summary>The task result's <c>response.errorMessage</c> with the target id replaced by <c>{id}</c>.</summary>
    private static string? ErrorShape(JsonElement result)
    {
        if (!result.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("errorMessage", out var message) || message.ValueKind != JsonValueKind.String)
            return null;
        var target = response.TryGetProperty("targetId", out var id) ? id.GetString() : null;
        var text = message.GetString()!;
        return string.IsNullOrEmpty(target) ? text : text.Replace(target, "{id}", StringComparison.OrdinalIgnoreCase);
    }

    // ── partner seeding and direct reads ─────────────────────────────────────

    /// <summary>
    /// Seeds the population (same seven rows as <see cref="RowSecurityLabTestBase.SeedAsync"/>) on partner
    /// <c>rs-xd-target</c> as <c>hq-auditor</c>, and asserts an <c>hq-auditor</c> lists exactly those seven ids there.
    /// </summary>
    private async Task<Population> SeedPartnerAsync()
    {
        var testId = $"rs-{Guid.NewGuid():N}"[..20];

        var r1 = await StartPartnerRowAsync(testId, "alice", null, "BR-001", 100, "north", "alice", "POS-A");
        var r2 = await StartPartnerRowAsync(testId, "carol", null, "BR-001", 200, "north", "carol", "POS-B");
        var r3 = await StartPartnerRowAsync(testId, "alice", "bob", "BR-002", 300, "south", "alice", "POS-A");
        var r4 = await StartPartnerRowAsync(testId, "carol", null, "BR-002", 400, "south", "carol", "POS-B");
        var r5 = await StartPartnerRowAsync(testId, Seeder, null, "BR-003", 500, "east", "dave", "POS-C");
        var r6 = await StartPartnerRowAsync(testId, Seeder, null, "BR-999", 10, "west", "erin", "POS-C");
        var r7 = await StartPartnerRowAsync(testId, Seeder, null, null, 700, "north", "frank", "POS-A");

        await PartnerTransitionAsync(r4, "rs-close", new { });
        await PartnerTransitionAsync(r5, "rs-update", new { attributes = new { branchCode = "BR-001" } });

        var population = new Population(testId, r1, r2, r3, r4, r5, r6, r7);
        var (status, body) = await PartnerListAsync(population, Hq());
        Assert.True(status == HttpStatusCode.OK, $"partner seed check list answered {(int)status}: {body}");
        Assert.True(population.All.ToHashSet().SetEquals(Ids(body)),
            $"partner seed check: hq-auditor did not list exactly the 7 seeded rows: {body}");
        return population;
    }

    private async Task<Guid> StartPartnerRowAsync(
        string testId, string actSub, string? sub, string? branchCode, decimal amount, string region, string owner,
        string positionCode, string? key = null)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["testId"] = testId, ["amount"] = amount, ["region"] = region, ["owner"] = owner, ["positionCode"] = positionCode,
        };
        if (branchCode is not null) attributes["branchCode"] = branchCode;
        object payload = key is null ? new { attributes } : new { key, attributes };

        var (status, body) = await PartnerSendAsync(HttpMethod.Post,
            $"api/v1/{PartnerDomain}/workflows/{Target}/instances/start?sync=true",
            new Caller(Sub: sub, ActSub: actSub, Role: HqAuditor), payload);
        Assert.True((int)status < 400, $"partner seed start refused with {(int)status}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.TryGetProperty("status", out var s) && s.GetString() == "A", $"partner seed start not Active: {body}");
        return Guid.Parse(root.GetProperty("id").GetString()!);
    }

    private async Task PartnerTransitionAsync(Guid id, string transition, object body)
    {
        var (status, response) = await PartnerSendAsync(HttpMethod.Patch,
            $"api/v1/{PartnerDomain}/workflows/{Target}/instances/{id}/transitions/{transition}?sync=true", Hq(), body);
        Assert.True((int)status < 400, $"partner '{transition}' on {id} refused with {(int)status}: {response}");
    }

    private Task<(HttpStatusCode Status, string Body)> PartnerListAsync(Population p, Caller caller)
    {
        var filter = JsonSerializer.Serialize(new { attributes = new { testId = new { eq = p.TestId } } });
        return PartnerSendAsync(HttpMethod.Get,
            $"api/v1/{PartnerDomain}/workflows/{Target}/instances?pageSize=100&filter={Uri.EscapeDataString(filter)}", caller);
    }

    private Task<(HttpStatusCode Status, string Body)> PartnerGetAsync(string idOrKey, Caller caller) =>
        PartnerSendAsync(HttpMethod.Get, $"api/v1/{PartnerDomain}/workflows/{Target}/instances/{Uri.EscapeDataString(idOrKey)}", caller);

    /// <summary>The <c>createdBy</c> of the latest <paramref name="transition"/> record of a partner instance.</summary>
    private async Task<string?> LastTransitionActorAsync(Guid id, string transition)
    {
        var (status, body) = await PartnerSendAsync(HttpMethod.Get,
            $"api/v1/{PartnerDomain}/workflows/{Target}/instances/{id}/transitions", Hq());
        Assert.True(status == HttpStatusCode.OK, $"partner transition history of {id} answered {(int)status}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("transitions");
        string? actor = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("transitionId", out var t) && t.GetString() == transition
                && item.TryGetProperty("createdBy", out var by))
                actor = by.GetString();
        }
        Assert.True(actor is not null, $"no '{transition}' record with createdBy in the partner history: {body}");
        return actor;
    }

    private async Task AssertPartnerTouchedAsync(Guid id, bool expectTouched)
    {
        var (status, body) = await PartnerGetAsync(id.ToString(), Hq());
        Assert.True(status == HttpStatusCode.OK, $"partner GET {id} as hq answered {(int)status}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.NotEqual("F", root.GetProperty("metadata").GetProperty("status").GetString());
        var touched = root.GetProperty("attributes").TryGetProperty("touchedBy", out var t) && t.GetString() == "rs-reader";
        Assert.True(touched == expectTouched, $"partner {id}: touchedBy expected {expectTouched}: {body}");
    }

    private async Task<(HttpStatusCode Status, string Body)> PartnerSendAsync(
        HttpMethod method, string url, Caller caller, object? body = null)
    {
        var http = _partner ?? throw new InvalidOperationException("partner client unavailable");
        if (!string.IsNullOrEmpty(caller.Query)) url += (url.Contains('?') ? "&" : "?") + caller.Query;

        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("x-device-id", "integration-test-device");
        request.Headers.TryAddWithoutValidation("x-token-id", "integration-test-token");
        if (caller.Sub is not null) request.Headers.TryAddWithoutValidation("sub", caller.Sub);
        if (caller.ActSub is not null) request.Headers.TryAddWithoutValidation("act_sub", caller.ActSub);
        if (caller.Role is not null) request.Headers.TryAddWithoutValidation("role", caller.Role);
        if (caller.Headers is not null)
            foreach (var (k, v) in caller.Headers) request.Headers.TryAddWithoutValidation(k, v);

        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static List<Guid> Ids(string listBody) =>
        JsonDocument.Parse(listBody).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => Guid.Parse(i.GetProperty("id").GetString()!)).ToList();
}
