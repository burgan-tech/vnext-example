using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Single GET, ETag and versions: E-1…E-6, E-7a/b/c, E-8. Definition A (<c>rs-a</c>) unless noted; "own (BR-001)"
/// = <c>{r1, r2, r5}</c> on latest data (R-F2).
/// <para>
/// E-7 moves the <c>sys-version-registry</c> pointer of <c>rs-versioned</c> (the rule set is read from
/// <c>GetFlowAsync(domain, workflow, null)</c>, K2). The class stays in the suite's single <c>VNextIntegration</c>
/// collection — every test class of this assembly is in it, so no other test runs while a pointer is moved — and
/// every E-7 row returns the pointer to Automatic in <c>finally</c>. Default-provider run only.
/// </para>
/// </summary>
[Collection("VNextIntegration")]
public class SingleGetTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";
    private const string Versioned = "rs-versioned";

    /// <summary>The runtime propagates a pointer change within ~5 s per pod; wait three times that.</summary>
    private static readonly TimeSpan Propagation = TimeSpan.FromSeconds(15);

    public SingleGetTests(VNextTestEnvironment environment) : base(environment) { }

    private static Caller BranchStaff(string code) =>
        new(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch(code));

    // ── ETag ─────────────────────────────────────────────────────────────────

    /// <summary>E-1: an excluded caller (BR-001) sends the <c>If-None-Match</c> an allowed caller got for r3 ⇒ 404, not 304.</summary>
    [SkippableFact]
    public async Task E1_ExcludedCallerWithAllowedCallersETag_Gets404Not304()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var (allowed, allowedBody, etag) = await GetAsync(Flow, p.R3.ToString(), Hq());
        Assert.True(allowed == HttpStatusCode.OK && etag is not null, $"hq GET r3 answered {(int)allowed}, ETag {etag}: {allowedBody}");
        Assert.Equal(HttpStatusCode.NotModified, (await GetAsync(Flow, p.R3.ToString(), Hq(), etag)).Status);

        var (status, body, _) = await GetAsync(Flow, p.R3.ToString(), BranchStaff("BR-001"), etag);
        Assert.True(status == HttpStatusCode.NotFound, $"excluded conditional GET answered {(int)status}: {body}");
    }

    /// <summary>E-2: excluded caller with <c>If-None-Match: *</c> ⇒ 404.</summary>
    [SkippableFact]
    public async Task E2_ExcludedCallerWithWildcardIfNoneMatch_Gets404()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var (status, body, _) = await GetAsync(Flow, p.R3.ToString(), BranchStaff("BR-001"), "*");
        Assert.True(status == HttpStatusCode.NotFound, $"If-None-Match * answered {(int)status}: {body}");
    }

    /// <summary>E-3: allowed caller (BR-001 on r1) with its own matching ETag ⇒ 304 (the probe does not break caching).</summary>
    [SkippableFact]
    public async Task E3_AllowedCallerWithMatchingETag_Gets304()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var caller = BranchStaff("BR-001");

        var (status, body, etag) = await GetAsync(Flow, p.R1.ToString(), caller);
        Assert.True(status == HttpStatusCode.OK && etag is not null, $"GET r1 answered {(int)status}, ETag {etag}: {body}");

        var (conditional, conditionalBody, _) = await GetAsync(Flow, p.R1.ToString(), caller, etag);
        Assert.True(conditional == HttpStatusCode.NotModified, $"conditional GET answered {(int)conditional}: {conditionalBody}");
    }

    // ── 404 shape ────────────────────────────────────────────────────────────

    /// <summary>
    /// E-4: GET by KEY of an excluded instance vs an unknown key ⇒ the same 404 body once the identifier and the trace
    /// id are normalised, and neither carries <c>ETag</c> or <c>X-Entity-ETag</c>.
    /// </summary>
    [SkippableFact]
    public async Task E4_ExcludedKeyAndUnknownKey_AnswerTheSame404()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var excludedKey = $"rs-e4-{Guid.NewGuid():N}";
        var unknownKey = $"rs-e4-{Guid.NewGuid():N}";

        var (started, startBody, _) = await SendAsCallerAsync(HttpMethod.Post,
            $"api/v1/{Domain}/workflows/{Flow}/instances/start?sync=true", Hq(),
            new { key = excludedKey, attributes = new { testId = p.TestId, branchCode = "BR-002", amount = 1 } });
        Assert.True((int)started < 400, $"keyed start answered {(int)started}: {startBody}");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Flow, excludedKey, Hq())).Status);

        var caller = BranchStaff("BR-001");
        var excluded = await SendAsCallerAsync(HttpMethod.Get, $"api/v1/{Domain}/workflows/{Flow}/instances/{excludedKey}", caller);
        var unknown = await SendAsCallerAsync(HttpMethod.Get, $"api/v1/{Domain}/workflows/{Flow}/instances/{unknownKey}", caller);

        Assert.True(excluded.Status == HttpStatusCode.NotFound, $"excluded key answered {(int)excluded.Status}: {excluded.Body}");
        Assert.True(unknown.Status == HttpStatusCode.NotFound, $"unknown key answered {(int)unknown.Status}: {unknown.Body}");
        Assert.Equal(Normalise(unknown.Body, unknownKey), Normalise(excluded.Body, excludedKey));
        foreach (var header in new[] { "ETag", "X-Entity-ETag" })
        {
            Assert.False(excluded.Headers.ContainsKey(header), $"excluded 404 carries {header}");
            Assert.False(unknown.Headers.ContainsKey(header), $"unknown 404 carries {header}");
        }
    }

    // ── data version, flow route ─────────────────────────────────────────────

    /// <summary>
    /// E-5: <c>?version=&lt;old&gt;</c> of r5 (old data BR-003, latest BR-001) ⇒ BR-001 caller 200 with the OLD data;
    /// BR-003 caller 404: the probe evaluates latest data, whatever data version is asked for.
    /// </summary>
    [SkippableFact]
    public async Task E5_OldDataVersion_IsJudgedOnLatestData()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var old = await OldDataVersionAsync(Flow, p.R5);
        var query = "version=" + Uri.EscapeDataString(old);

        var (status, body, _) = await GetAsync(Flow, p.R5.ToString(), BranchStaff("BR-001") with { Query = query });
        Assert.True(status == HttpStatusCode.OK, $"BR-001 GET r5?{query} answered {(int)status}: {body}");
        Assert.Equal("BR-003", JsonDocument.Parse(body).RootElement.GetProperty("attributes").GetProperty("branchCode").GetString());

        var (denied, deniedBody, _) = await GetAsync(Flow, p.R5.ToString(), BranchStaff("BR-003") with { Query = query });
        Assert.True(denied == HttpStatusCode.NotFound, $"BR-003 GET r5?{query} answered {(int)denied}: {deniedBody}");
    }

    /// <summary>E-6: r1 of <c>rs-a</c> addressed through <c>/workflows/rs-plain/instances/{r1}</c> ⇒ 404 (any caller).</summary>
    [SkippableFact]
    public async Task E6_InstanceThroughAnotherFlowsRoute_Gets404()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Flow, p.R1.ToString(), Hq())).Status);
        foreach (var caller in new[] { Hq(), BranchStaff("BR-001") })
        {
            var (status, body, _) = await GetAsync("rs-plain", p.R1.ToString(), caller);
            Assert.True(status == HttpStatusCode.NotFound, $"rs-plain/{p.R1} answered {(int)status}: {body}");
        }
    }

    // ── rule-set version (K2) ────────────────────────────────────────────────

    /// <summary>
    /// E-7a: no registry pointer (Automatic) ⇒ the LATEST version's rule (1.0.1: <c>branch-staff → region eq
    /// x-region</c>) applies to instances started on 1.0.0 AND on 1.0.1, on list and GET: a <c>branch-staff</c> +
    /// <c>x-branch-code</c> caller sees nothing, a <c>branch-staff</c> + <c>x-region: north</c> caller sees
    /// <c>{r1, r2, r7}</c>.
    /// </summary>
    [SkippableFact]
    public async Task E7a_NoPointer_LatestRuleAppliesToInstancesOfEveryVersion()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await PublishVersionedV101Async();
        await Registry.EnsureRegistryAsync();
        try
        {
            await Registry.ClearPointerAsync(Versioned);
            var (v100, v101) = await SeedBothVersionsAsync();
            await WaitForRuleAsync(v101, Region("north"), v101.R1, v101.R2, v101.R7);

            foreach (var p in new[] { v100, v101 })
            {
                UsePopulation(Versioned, p);
                await AssertVisibleAsync(Versioned, p, BranchStaff("BR-001"));
                await AssertVisibleAsync(Versioned, p, Region("north"), p.R1, p.R2, p.R7);
            }
        }
        finally
        {
            await Registry.ClearPointerAsync(Versioned);
        }
    }

    /// <summary>
    /// E-7b: pointer 1.0.0 ⇒ 1.0.0's rule (<c>branchCode eq x-branch-code</c>) for instances of BOTH versions, on list
    /// and GET (list ids == the ids the GET answers 200 for): BR-001 ⇒ own, <c>x-region</c> alone ⇒ none.
    /// </summary>
    [SkippableFact]
    public async Task E7b_PointerPinsOldVersion_ItsRuleAppliesToInstancesOfEveryVersion()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await PublishVersionedV101Async();
        await Registry.EnsureRegistryAsync();
        try
        {
            await Registry.SetPointerAsync(Versioned, "1.0.0");
            var (v100, v101) = await SeedBothVersionsAsync();
            await WaitForRuleAsync(v101, BranchStaff("BR-001"), v101.R1, v101.R2, v101.R5);

            foreach (var p in new[] { v100, v101 })
            {
                UsePopulation(Versioned, p);
                await AssertVisibleAsync(Versioned, p, BranchStaff("BR-001"), p.R1, p.R2, p.R5);
                await AssertVisibleAsync(Versioned, p, Region("north"));
            }
        }
        finally
        {
            await Registry.ClearPointerAsync(Versioned);
        }
    }

    /// <summary>
    /// E-7c: <c>set-default "9.9"</c> (no such version) is refused AT WRITE with 409 <c>Instance:100052</c>
    /// (<c>VersionRegistryWriteGuard</c>), and list and GET keep answering with the previous pointer's (1.0.0) rule.
    /// A stored invalid pointer at read time is not reachable through the public API (every write is guarded,
    /// versions are never unpublished). Such a lookup error fails list, GET and the GetInstance(s) tasks of every
    /// flow with the error as is (D5, confirmed 2026-10-27), span tag <c>vnext.rowsecurity.outcome=denied-flow-load</c>,
    /// warning 20509; vnext unit tests pin it.
    /// </summary>
    [SkippableFact]
    public async Task E7c_InvalidPointerRefusedAtWrite_PreviousRuleStaysInForce()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await PublishVersionedV101Async();
        await Registry.EnsureRegistryAsync();
        try
        {
            await Registry.SetPointerAsync(Versioned, "1.0.0");
            var (v100, v101) = await SeedBothVersionsAsync();
            await WaitForRuleAsync(v101, BranchStaff("BR-001"), v101.R1, v101.R2, v101.R5);

            var (status, body) = await Registry.TrySetPointerAsync(Versioned, "9.9");
            Assert.True(status == HttpStatusCode.Conflict && body.Contains("Instance:100052", StringComparison.Ordinal),
                $"set-default 9.9 answered {(int)status}: {body}");

            foreach (var p in new[] { v100, v101 })
            {
                UsePopulation(Versioned, p);
                await AssertVisibleAsync(Versioned, p, BranchStaff("BR-001"), p.R1, p.R2, p.R5);
                await AssertVisibleAsync(Versioned, p, Region("north"));
            }
        }
        finally
        {
            await Registry.ClearPointerAsync(Versioned);
        }
    }

    // ── latest data ──────────────────────────────────────────────────────────

    /// <summary>
    /// E-8: r5 after updateData BR-003 → BR-001 is judged on its latest row: on Definition A the BR-001 caller sees
    /// it and the BR-003 caller sees nothing; on Definition B (mixed OR with <c>$InstanceStarter</c>) alice + BR-001
    /// sees <c>{r1, r2, r3, r5}</c>, alice + BR-003 only her own <c>{r1, r3}</c>.
    /// </summary>
    [SkippableFact]
    public async Task E8_UpdatedRow_IsJudgedOnLatestData_AlsoUnderMixedOr()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var a = await SeedAsync(Flow);
        await AssertVisibleAsync(Flow, a, BranchStaff("BR-001"), a.R1, a.R2, a.R5);
        await AssertVisibleAsync(Flow, a, BranchStaff("BR-003"));

        var b = await SeedAsync("rs-b");
        await AssertVisibleAsync("rs-b", b, BranchStaff("BR-001"), b.R1, b.R2, b.R3, b.R5);
        await AssertVisibleAsync("rs-b", b, BranchStaff("BR-003"), b.R1, b.R3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Caller Region(string region) =>
        new(Sub: "alice", ActSub: "alice", Role: "branch-staff",
            Headers: new Dictionary<string, string> { ["x-region"] = region });

    /// <summary>Seeds <c>rs-versioned</c> on 1.0.0 and on 1.0.1 (explicit <c>?version=</c>) and checks where each landed.</summary>
    private async Task<(Population V100, Population V101)> SeedBothVersionsAsync()
    {
        var v100 = await SeedAsync(Versioned, "1.0.0");
        await AssertFlowVersionAsync(v100.R1, "1.0.0");
        var v101 = await SeedAsync(Versioned, VersionedV101);
        await AssertFlowVersionAsync(v101.R1, VersionedV101);
        return (v100, v101);
    }

    private async Task AssertFlowVersionAsync(Guid id, string version)
    {
        var (status, body, _) = await GetAsync(Versioned, id.ToString(), Hq());
        Assert.True(status == HttpStatusCode.OK, $"hq GET {Versioned}/{id} answered {(int)status}: {body}");
        var stored = JsonDocument.Parse(body).RootElement.GetProperty("flowVersion").GetString() ?? "";
        Assert.True(stored == version || stored.StartsWith(version + "-", StringComparison.Ordinal)
                                       || stored.StartsWith(version + "+", StringComparison.Ordinal),
            $"{Versioned}/{id} started on {stored}, expected {version}");
    }

    /// <summary>Waits until the pointer change reached the read path: the list of <paramref name="p"/> equals the set.</summary>
    private async Task WaitForRuleAsync(Population p, Caller caller, params Guid[] expected)
    {
        UsePopulation(Versioned, p);
        IReadOnlyList<Guid> last = [];
        await WaitUntilAsync(async () =>
        {
            last = await ListIdsAsync(Versioned, caller);
            return expected.ToHashSet().SetEquals(last);
        }, $"{Versioned}: the rule never switched; expected [{Names(p, expected)}]", Propagation);
    }

    /// <summary>The non-latest data version of an instance, from its data history (read as hq).</summary>
    private async Task<string> OldDataVersionAsync(string flow, Guid id)
    {
        var (status, body, _) = await SendAsCallerAsync(HttpMethod.Get,
            $"api/v1/{Domain}/workflows/{flow}/instances/{id}/data/history", Hq());
        Assert.True(status == HttpStatusCode.OK, $"data history answered {(int)status}: {body}");
        foreach (var row in JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray())
            if (!row.GetProperty("isLatest").GetBoolean())
                return row.GetProperty("version").GetString()!;

        Assert.Fail($"{flow}/{id} has no older data version: {body}");
        return null!;
    }

    /// <summary>A problem body with the identifier replaced and the trace id removed.</summary>
    private static string Normalise(string body, string identifier)
    {
        var node = JsonNode.Parse(body.Replace(identifier, "<identifier>", StringComparison.Ordinal))!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString();
    }
}
