using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Same-domain trigger tasks against Definition A (<c>rs-a</c>), driven through <c>rs-reader</c>: GetInstances
/// (<c>read-list</c>), GetInstance (<c>read-one</c>) and DirectTrigger (<c>trigger-one</c>). Rows T-1…T-6 and T-8
/// (T-7 is a morph-idm row, 11G).
/// <para>
/// A trigger task does NOT forward rule headers on its own (council decision 2 + user decision B): only the
/// credential (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>, <c>role</c>) travels. A rule that reads
/// <c>x-branch-code</c> sees it only when the task mapping sets it; a non-empty mapped value wins over the caller's.
/// </para>
/// <para>
/// Each task row asserts both halves through the task: the GetInstances ids equal the expected set, and GetInstance
/// answers 200 for every expected id and 404 for every other population id. "Own (BR-001)" is evaluated on latest
/// data: <c>{r1, r2, r5}</c> (R-F2). Default-provider run only.
/// </para>
/// </summary>
public class TriggerTaskTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";

    public TriggerTaskTests(VNextTestEnvironment environment) : base(environment) { }

    private static Caller BranchStaff(string branch) =>
        new(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch(branch));

    /// <summary>
    /// T-1: caller <c>branch-staff</c> + <c>x-branch-code: BR-001</c>, task without a header mapping ⇒ none. The
    /// role travels, <c>x-branch-code</c> does not, so the branch rule has no parameter and contributes nothing.
    /// </summary>
    [SkippableFact]
    public async Task T1_BranchStaff_NoHeaderMapping_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertTaskVisibleAsync(p, BranchStaff("BR-001"), mapped: null);
    }

    /// <summary>T-2: same caller, the mapping sets <c>x-branch-code: BR-001</c> ⇒ own branch <c>{r1, r2, r5}</c>.</summary>
    [SkippableFact]
    public async Task T2_BranchStaff_HeaderMapped_SeesOwnBranch()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertTaskVisibleAsync(p, BranchStaff("BR-001"), Branch("BR-001"), p.R1, p.R2, p.R5);
    }

    /// <summary>T-3: caller <c>hq-auditor</c>, no mapping ⇒ all seven (the forwarded role opens the unfiltered rule).</summary>
    [SkippableFact]
    public async Task T3_HqAuditor_NoMapping_SeesAll()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertTaskVisibleAsync(p, Hq(), mapped: null, [.. p.All]);
    }

    /// <summary>T-4: role-less caller (identity alice), no mapping ⇒ none (K1: deny by default).</summary>
    [SkippableFact]
    public async Task T4_Roleless_NoMapping_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertTaskVisibleAsync(p, new Caller(Sub: "alice", ActSub: "alice"), mapped: null);
    }

    /// <summary>
    /// T-5: caller <c>hq-auditor</c>, the mapping sets <c>role: branch-staff</c> + <c>x-branch-code: BR-001</c> ⇒ own
    /// branch <c>{r1, r2, r5}</c>: the non-empty mapped role replaces the caller's, it does not add to it.
    /// </summary>
    [SkippableFact]
    public async Task T5_HqAuditor_MappingSetsBranchStaff_MappingWins()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var mapped = new Dictionary<string, string> { ["role"] = "branch-staff", ["x-branch-code"] = "BR-001" };
        await AssertTaskVisibleAsync(p, Hq(), mapped, p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// T-6: <c>branch-staff</c> BR-001 (mapped) GetInstance on r3 (BR-002) ⇒ the task records 404, the same status
    /// (and error text, id aside) a nonexistent id records — exclusion is not distinguishable from absence.
    /// </summary>
    [SkippableFact]
    public async Task T6_GetInstanceOfExcludedRow_EqualsNonexistent()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var caller = BranchStaff("BR-001");

        var excluded = await ReadThroughReaderAsync(caller, "read-one",
            new { targetFlow = Flow, targetId = p.R3.ToString(), headers = Branch("BR-001") });
        var missing = await ReadThroughReaderAsync(caller, "read-one",
            new { targetFlow = Flow, targetId = Guid.NewGuid().ToString(), headers = Branch("BR-001") });

        Assert.Equal(404, StatusCode(excluded));
        Assert.Equal(StatusCode(missing), StatusCode(excluded));
        Assert.Equal(missing.GetProperty("isSuccess").ToString(), excluded.GetProperty("isSuccess").ToString());
        Assert.Equal(ErrorShape(missing), ErrorShape(excluded));
        Assert.NotNull(ErrorShape(excluded));

        // Control: the same caller and mapping does see r1 through the same task.
        var visible = await ReadThroughReaderAsync(caller, "read-one",
            new { targetFlow = Flow, targetId = p.R1.ToString(), headers = Branch("BR-001") });
        Assert.Equal(200, StatusCode(visible));
    }

    /// <summary>
    /// T-8 (decision 5): a role-less caller DirectTriggers <c>rs-update</c> into <c>rs-a</c> by KEY ⇒ the transition
    /// succeeds and the target is not faulted (the key → id lookup goes through the local id-only port, which row
    /// security does not gate; the transition itself is not row-gated). By id ⇒ succeeds as well.
    /// </summary>
    [SkippableFact]
    public async Task T8_RolelessDirectTrigger_ByKeyAndById_Succeeds()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var roleless = new Caller(Sub: "alice", ActSub: "alice");

        // Precondition: the role-less caller cannot see the targets through the list or GET.
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
    /// One task row: GetInstances (scoped to the population's testId) lists exactly <paramref name="expected"/>, and
    /// GetInstance answers 200 for each expected id and 404 for every other population id — both with the same
    /// caller and the same <paramref name="mapped"/> headers (null = no mapping).
    /// </summary>
    private async Task AssertTaskVisibleAsync(
        Population p, Caller caller, IDictionary<string, string>? mapped, params Guid[] expected)
    {
        object listBody = mapped is null
            ? new { targetFlow = Flow, testId = p.TestId }
            : new { targetFlow = Flow, testId = p.TestId, headers = mapped };
        var list = await ReadThroughReaderAsync(caller, "read-list", listBody);
        Assert.True(list.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"GetInstances task result: {list}");
        var ids = list.GetProperty("ids").EnumerateArray().Select(id => Guid.Parse(id.GetString()!)).ToList();
        Assert.True(expected.ToHashSet().SetEquals(ids),
            $"GetInstances {Flow}: expected [{Names(p, expected)}], got [{Names(p, ids)}]");

        foreach (var id in p.All)
        {
            object getBody = mapped is null
                ? new { targetFlow = Flow, targetId = id.ToString() }
                : new { targetFlow = Flow, targetId = id.ToString(), headers = mapped };
            var get = await ReadThroughReaderAsync(caller, "read-one", getBody);
            var want = expected.Contains(id) ? 200 : 404;
            Assert.True(StatusCode(get) == want,
                $"GetInstance {Flow}/{Names(p, [id])} recorded {StatusCode(get)}, expected {want}: {get}");
        }
    }

    private static int? StatusCode(JsonElement result) =>
        result.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : null;

    /// <summary>
    /// The task result's <c>response.errorMessage</c> with the target id replaced by <c>{id}</c> (the task records
    /// <c>Instance "{id}" not found</c>), so an excluded and a nonexistent id compare equal only if the texts match.
    /// </summary>
    private static string? ErrorShape(JsonElement result)
    {
        if (!result.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("errorMessage", out var message) || message.ValueKind != JsonValueKind.String)
            return null;
        var target = response.TryGetProperty("targetId", out var id) ? id.GetString() : null;
        var text = message.GetString()!;
        return string.IsNullOrEmpty(target) ? text : text.Replace(target, "{id}", StringComparison.OrdinalIgnoreCase);
    }

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
