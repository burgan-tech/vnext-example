using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Known-gap pins K-1…K-4 (phase-1 scope, AR-1 / D2 in docs/domain/row-security.md § Known gaps). Row security
/// covers the instance list and the single GET only. These rows assert TODAY's behaviour of the uncovered surfaces
/// so that closing a gap (phase 2) shows up as a deliberate test change, not as silent drift:
/// <list type="bullet">
/// <item>K-1: <c>functions/state</c> of an id the caller's GET hides answers 200.</item>
/// <item>K-2: <c>functions/data</c> of the same id answers 200 (the existence oracle next to GET 404).</item>
/// <item>K-3: the GetInstanceData task returns that id's data.</item>
/// <item>K-4: an opted-in parent's SubFlow child, in a flow without <c>rowSecurity</c>, is listed unfiltered (the
/// child does not inherit the parent's rules; publish logs RS-W7 — log-only, R-F8 — not asserted here).</item>
/// </list>
/// Default-provider run only.
/// </summary>
public class KnownGapPinTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";
    private const string K4Parent = "rs-k4-parent";
    private const string K4Child = "rs-k4-child";

    public KnownGapPinTests(VNextTestEnvironment environment) : base(environment) { }

    private static Caller Roleless() => new(Sub: "alice", ActSub: "alice");

    /// <summary>K-1 [pin]: the state function of r1, which the role-less caller's GET hides (404), answers 200.</summary>
    [SkippableFact]
    public async Task K1_Pinned_StateFunctionOfExcludedId_Answers200()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        await AssertExcludedFromGetAsync(p.R1);

        var (status, body, _) = await SendAsCallerAsync(HttpMethod.Get,
            $"api/v1/{Domain}/workflows/{Flow}/instances/{p.R1}/functions/state", Roleless());

        Assert.True(status == HttpStatusCode.OK, $"functions/state answered {(int)status}: {body}");
    }

    /// <summary>K-2 [pin]: the data function of the same excluded id answers 200 and carries its data.</summary>
    [SkippableFact]
    public async Task K2_Pinned_DataFunctionOfExcludedId_Answers200()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        await AssertExcludedFromGetAsync(p.R1);

        var (status, body, _) = await SendAsCallerAsync(HttpMethod.Get,
            $"api/v1/{Domain}/workflows/{Flow}/instances/{p.R1}/functions/data", Roleless());

        Assert.True(status == HttpStatusCode.OK, $"functions/data answered {(int)status}: {body}");
        Assert.Contains(p.TestId, body, StringComparison.Ordinal);
    }

    /// <summary>K-3 [pin]: the GetInstanceData task (as the same role-less caller) returns the excluded id's data.</summary>
    [SkippableFact]
    public async Task K3_Pinned_GetInstanceDataTaskOnExcludedId_ReturnsData()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        await AssertExcludedFromGetAsync(p.R1);

        var result = await ReadThroughReaderAsync(Roleless(), "read-data",
            new { targetFlow = Flow, targetId = p.R1.ToString() });

        Assert.True(result.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"task result: {result}");
        Assert.True(result.GetProperty("statusCode").ValueKind == JsonValueKind.Number
                    && result.GetProperty("statusCode").GetInt32() == 200, $"task result: {result}");
        Assert.Contains(p.TestId, result.GetProperty("response").GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// K-4 [pin]: <c>rs-k4-parent</c> (Definition A) starts <c>rs-k4-child</c> (no <c>rowSecurity</c>) from its SubFlow
    /// state. A role-less caller sees nothing of the parent but lists and GETs the child: the child flow is read
    /// through its own (absent) rules. The RS-W7 publish warning is log-only and not part of the response (R-F8).
    /// </summary>
    [SkippableFact]
    public async Task K4_Pinned_SubFlowChildOfOptedInParent_IsUnfiltered()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var testId = $"rs-k4-{Guid.NewGuid():N}"[..20];

        var (started, startBody, _) = await SendAsCallerAsync(HttpMethod.Post,
            $"api/v1/{Domain}/workflows/{K4Parent}/instances/start?sync=true", Hq(),
            new { attributes = new { testId, branchCode = "BR-001" } });
        Assert.True((int)started < 400, $"{K4Parent} start refused with {(int)started}: {startBody}");
        var parent = JsonDocument.Parse(startBody).RootElement.GetProperty("id").GetString()!;

        var child = await WaitForChildAsync(testId);

        // The parent is opted in: the role-less caller sees it neither in the list nor on GET.
        var parentList = await ListByTestIdAsync(K4Parent, testId, Roleless());
        Assert.Empty(parentList);
        var (parentGet, parentBody, _) = await GetAsync(K4Parent, parent, Roleless());
        Assert.True(parentGet == HttpStatusCode.NotFound, $"GET {K4Parent}/{parent} answered {(int)parentGet}: {parentBody}");

        // The child is not: unfiltered list and GET.
        var childList = await ListByTestIdAsync(K4Child, testId, Roleless());
        Assert.Equal([child], childList);
        var (childGet, childBody, _) = await GetAsync(K4Child, child.ToString(), Roleless());
        Assert.True(childGet == HttpStatusCode.OK, $"GET {K4Child}/{child} answered {(int)childGet}: {childBody}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Precondition of K-1…K-3: the role-less caller's GET of <paramref name="id"/> answers 404.</summary>
    private async Task AssertExcludedFromGetAsync(Guid id)
    {
        var (status, body, _) = await GetAsync(Flow, id.ToString(), Roleless());
        Assert.True(status == HttpStatusCode.NotFound, $"precondition: GET {Flow}/{id} answered {(int)status}: {body}");
    }

    private async Task<IReadOnlyList<Guid>> ListByTestIdAsync(string flow, string testId, Caller caller)
    {
        var filter = JsonSerializer.Serialize(new { attributes = new { testId = new { eq = testId } } });
        var (status, body, _) = await SendAsCallerAsync(HttpMethod.Get,
            $"api/v1/{Domain}/workflows/{flow}/instances?pageSize=100&filter={Uri.EscapeDataString(filter)}", caller);
        Assert.True(status == HttpStatusCode.OK, $"list {flow} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("id").GetString()!)).ToList();
    }

    /// <summary>The SubFlow child is started by a job after the parent's commit: poll (as hq) until it exists.</summary>
    private async Task<Guid> WaitForChildAsync(string testId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var children = await ListByTestIdAsync(K4Child, testId, Hq());
            if (children.Count == 1) return children[0];
            Assert.True(children.Count == 0, $"{K4Child} has {children.Count} rows for {testId}");
            Assert.True(DateTime.UtcNow < deadline, $"{K4Child} for {testId} did not appear within 30 s");
            await Task.Delay(500);
        }
    }
}
