using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Same-domain trigger tasks against Definition A (<c>rs-a</c>), driven through <c>rs-reader</c>: GetInstances
/// (<c>read-list</c>), GetInstance (<c>read-one</c>) and DirectTrigger (<c>trigger-one</c>). Rows T-1…T-6 and T-8
/// (T-7 is a morph-idm row, 11G).
/// <para>
/// Row security applies only on the public list and single-GET endpoints (vnext 7c9caf4a, user decision 2026-10-27).
/// A trigger task against the SAME domain runs through the local gateway, in process, and reads UNFILTERED: neither
/// the caller's role nor the mapped headers (<c>role</c>, <c>x-branch-code</c>) change what it sees. Each row asserts
/// that through both halves of the task — GetInstances lists all seven population ids, GetInstance answers 200 for
/// each — next to a direct-HTTP control showing the same caller IS filtered on the public endpoints. A task against
/// another domain lands on the partner's public endpoint and is filtered there (CrossDomainLab X-1…X-6).
/// Default-provider run only.
/// </para>
/// </summary>
public class TriggerTaskTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";

    public TriggerTaskTests(VNextTestEnvironment environment) : base(environment) { }

    private static Caller BranchStaff(string branch) =>
        new(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch(branch));

    /// <summary>
    /// T-1: caller <c>branch-staff</c> + <c>x-branch-code: BR-001</c> (directly: own branch), task without a header
    /// mapping ⇒ all seven through the task.
    /// </summary>
    [SkippableFact]
    public async Task T1_BranchStaff_NoHeaderMapping_TaskSeesAll()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, BranchStaff("BR-001"), p.R1, p.R2, p.R5);
        await AssertTaskSeesAllAsync(p, BranchStaff("BR-001"), mapped: null);
    }

    /// <summary>T-2: same caller, the mapping sets <c>x-branch-code: BR-001</c> ⇒ still all seven (the mapping filters nothing).</summary>
    [SkippableFact]
    public async Task T2_BranchStaff_HeaderMapped_TaskSeesAll()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertTaskSeesAllAsync(p, BranchStaff("BR-001"), Branch("BR-001"));
    }

    /// <summary>T-3: caller <c>hq-auditor</c>, no mapping ⇒ all seven (same as its direct read).</summary>
    [SkippableFact]
    public async Task T3_HqAuditor_NoMapping_TaskSeesAll()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertTaskSeesAllAsync(p, Hq(), mapped: null);
    }

    /// <summary>
    /// T-4: role-less caller (identity alice; directly: deny by default, nothing) ⇒ all seven through the task.
    /// </summary>
    [SkippableFact]
    public async Task T4_Roleless_NoMapping_TaskSeesAll()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var roleless = new Caller(Sub: "alice", ActSub: "alice");

        await AssertVisibleAsync(Flow, p, roleless);
        await AssertTaskSeesAllAsync(p, roleless, mapped: null);
    }

    /// <summary>
    /// T-5: caller <c>hq-auditor</c>, the mapping sets <c>role: branch-staff</c> + <c>x-branch-code: BR-001</c> ⇒ all
    /// seven: a mapped narrower role does not narrow a same-domain read.
    /// </summary>
    [SkippableFact]
    public async Task T5_HqAuditor_MappingSetsBranchStaff_TaskSeesAll()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var mapped = new Dictionary<string, string> { ["role"] = "branch-staff", ["x-branch-code"] = "BR-001" };
        await AssertTaskSeesAllAsync(p, Hq(), mapped);
    }

    /// <summary>
    /// T-6: <c>branch-staff</c> BR-001 (mapped) GetInstance on r3 (BR-002, which the same caller's direct GET answers
    /// 404) ⇒ the task records 200 with r3, while a nonexistent id still records 404: the task tells a real row from a
    /// missing one because it reads unfiltered.
    /// </summary>
    [SkippableFact]
    public async Task T6_GetInstanceOfRowHiddenFromDirectGet_IsReadByTask()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var caller = BranchStaff("BR-001");

        var (direct, directBody, _) = await GetAsync(Flow, p.R3.ToString(), caller);
        Assert.True(direct == HttpStatusCode.NotFound, $"precondition: direct GET r3 answered {(int)direct}: {directBody}");

        var hidden = await ReadThroughReaderAsync(caller, "read-one",
            new { targetFlow = Flow, targetId = p.R3.ToString(), headers = Branch("BR-001") });
        var missing = await ReadThroughReaderAsync(caller, "read-one",
            new { targetFlow = Flow, targetId = Guid.NewGuid().ToString(), headers = Branch("BR-001") });

        Assert.True(StatusCode(hidden) == 200 && hidden.GetProperty("isSuccess").ValueKind == JsonValueKind.True,
            $"GetInstance task on r3 recorded {StatusCode(hidden)}: {hidden}");
        Assert.Contains(p.R3.ToString(), hidden.GetProperty("response").GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(404, StatusCode(missing));
    }

    /// <summary>
    /// T-8: a role-less caller DirectTriggers <c>rs-update</c> into <c>rs-a</c> by KEY ⇒ the transition succeeds and the
    /// target is not faulted; by id ⇒ succeeds as well. Transitions are not row-gated (user decision 2026-10-27): the
    /// key → id lookup and the transition are in-process and exempt.
    /// </summary>
    [SkippableFact]
    public async Task T8_RolelessDirectTrigger_ByKeyAndById_Succeeds()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var roleless = new Caller(Sub: "alice", ActSub: "alice");

        // Precondition: the role-less caller cannot see the targets through the public list or GET.
        await AssertVisibleAsync(Flow, p, roleless);

        var key = $"rs-t8-{Guid.NewGuid():N}"[..24];
        var keyed = await StartKeyedAsync(key, p.TestId);

        var byKey = await ReadThroughReaderAsync(roleless, "trigger-one",
            new { targetFlow = Flow, targetKey = key, transitionName = "rs-update" });
        AssertTriggered(byKey, "by key");
        await AssertTouchedAndNotFaultedAsync(keyed);

        var byId = await ReadThroughReaderAsync(roleless, "trigger-one",
            new { targetFlow = Flow, targetId = p.R6.ToString(), transitionName = "rs-update" });
        AssertTriggered(byId, "by id");
        await AssertTouchedAndNotFaultedAsync(p.R6);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One task row: GetInstances (scoped to the population's testId) lists exactly the seven population ids, and
    /// GetInstance answers 200 for each — both with the same caller and the same <paramref name="mapped"/> headers
    /// (null = no mapping).
    /// </summary>
    private async Task AssertTaskSeesAllAsync(Population p, Caller caller, IDictionary<string, string>? mapped)
    {
        object listBody = mapped is null
            ? new { targetFlow = Flow, testId = p.TestId }
            : new { targetFlow = Flow, testId = p.TestId, headers = mapped };
        var list = await ReadThroughReaderAsync(caller, "read-list", listBody);
        Assert.True(list.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"GetInstances task result: {list}");
        var ids = list.GetProperty("ids").EnumerateArray().Select(id => Guid.Parse(id.GetString()!)).ToList();
        Assert.True(p.All.ToHashSet().SetEquals(ids),
            $"GetInstances {Flow}: expected all [{Names(p, p.All)}], got [{Names(p, ids)}]");

        foreach (var id in p.All)
        {
            object getBody = mapped is null
                ? new { targetFlow = Flow, targetId = id.ToString() }
                : new { targetFlow = Flow, targetId = id.ToString(), headers = mapped };
            var get = await ReadThroughReaderAsync(caller, "read-one", getBody);
            Assert.True(StatusCode(get) == 200,
                $"GetInstance {Flow}/{Names(p, [id])} recorded {StatusCode(get)}, expected 200: {get}");
        }
    }

    private static int? StatusCode(JsonElement result) =>
        result.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : null;

    private static void AssertTriggered(JsonElement result, string how)
    {
        Assert.True(result.GetProperty("isSuccess").ValueKind == JsonValueKind.True,
            $"DirectTrigger {how} did not succeed: {result}");
        var status = StatusCode(result);
        Assert.True(status is >= 200 and < 300, $"DirectTrigger {how} recorded {status}: {result}");
    }

    /// <summary>Starts one <c>rs-a</c> row with a business key (BR-002, invisible to a role-less caller anyway).</summary>
    private async Task<Guid> StartKeyedAsync(string key, string testId)
    {
        var (status, body, _) = await SendAsCallerAsync(HttpMethod.Post,
            $"api/v1/{Domain}/workflows/{Flow}/instances/start?sync=true", Hq(),
            new { key, attributes = new { testId = testId + "-k", branchCode = "BR-002", amount = 1, region = "south" } });
        Assert.True((int)status < 400, $"keyed start refused with {(int)status}: {body}");
        return Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!);
    }

    /// <summary>The target received the trigger's body (<c>touchedBy: rs-reader</c>) and is not faulted.</summary>
    private async Task AssertTouchedAndNotFaultedAsync(Guid id)
    {
        var (status, body, _) = await GetAsync(Flow, id.ToString(), Hq());
        Assert.True(status == HttpStatusCode.OK, $"GET {Flow}/{id} as hq answered {(int)status}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        var instanceStatus = root.GetProperty("metadata").GetProperty("status").GetString();
        Assert.True(instanceStatus != "F", $"{Flow}/{id} is faulted after the trigger: {body}");
        Assert.True(root.GetProperty("attributes").TryGetProperty("touchedBy", out var touched)
                    && touched.GetString() == "rs-reader", $"{Flow}/{id} was not updated by the trigger: {body}");
    }
}
