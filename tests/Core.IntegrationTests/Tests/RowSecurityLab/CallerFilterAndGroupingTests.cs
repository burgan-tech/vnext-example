using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// The row predicate meets the caller's own filter, paging and grouping (e2e wiring, list only): C-1…C-6, U-route,
/// G-1…G-4. Definition A (<c>rs-a</c>) unless noted; "own (BR-001)" = <c>{r1, r2, r5}</c> on latest data (R-F2).
/// <para>
/// Routes: a plain filter (or none) goes through the string list route (repository string overload); a filter
/// envelope carrying <c>groupBy</c> goes through the GraphQL route. Both meet the row predicate at the single
/// repository merge point. Default-provider run only.
/// </para>
/// </summary>
public class CallerFilterAndGroupingTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";
    private const string GroupByBranch = """{"fields":["attributes.branchCode"],"aggregations":{"count":true}}""";

    private const string GroupByBranchWithSum =
        """{"fields":["attributes.branchCode"],"aggregations":{"count":true,"sum":"attributes.amount"}}""";

    public CallerFilterAndGroupingTests(VNextTestEnvironment environment) : base(environment) { }

    private static Caller BranchStaff(string code) =>
        new(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch(code));

    private static string Attr(string field, string op, object value) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["attributes"] = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [op] = value } }
        });

    private static void AssertIds(Population p, IReadOnlyList<Guid> actual, string because, params Guid[] expected) =>
        Assert.True(expected.ToHashSet().SetEquals(actual),
            $"{because}: expected [{Names(p, expected)}], got [{Names(p, actual)}]");

    // ── caller filter ────────────────────────────────────────────────────────

    /// <summary>
    /// C-1: <c>branch-staff</c> BR-001 sends <c>{"or":[branchCode eq BR-002, testId eq &lt;run&gt;]}</c> — an OR that
    /// would admit the foreign branch — ⇒ own only: the rule is ANDed outside the caller's OR.
    /// </summary>
    [SkippableFact]
    public async Task C1_CallerOrDoesNotEscapeTheRule()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var filter = $"{{\"or\":[{Attr("branchCode", "eq", "BR-002")},{Attr("testId", "eq", p.TestId)}]}}";
        AssertIds(p, await ListIdsAsync(Flow, BranchStaff("BR-001"), filter), "C-1", p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// C-2: a caller <c>not</c> around <c>branchCode</c> never yields a foreign row: <c>not eq BR-002</c> ⇒ own;
    /// <c>not eq BR-001</c> ⇒ none (it cannot flip the rule).
    /// </summary>
    [SkippableFact]
    public async Task C2_CallerNotNeverYieldsAForeignRow()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        AssertIds(p, await ListIdsAsync(Flow, BranchStaff("BR-001"), $"{{\"not\":{Attr("branchCode", "eq", "BR-002")}}}"),
            "C-2 not BR-002", p.R1, p.R2, p.R5);
        AssertIds(p, await ListIdsAsync(Flow, BranchStaff("BR-001"), $"{{\"not\":{Attr("branchCode", "eq", "BR-001")}}}"),
            "C-2 not BR-001");
    }

    /// <summary>C-3: a narrowing caller filter (<c>amount gt 200</c>) ⇒ the intersection <c>{r5}</c>.</summary>
    [SkippableFact]
    public async Task C3_CallerNarrowingFilter_Intersects()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        AssertIds(p, await ListIdsAsync(Flow, BranchStaff("BR-001"), Attr("amount", "gt", 200)), "C-3", p.R5);
    }

    /// <summary>
    /// C-4: a caller filter of exactly 995 characters (the whole <c>filter</c> query value) plus the rule ⇒ 200 with
    /// own: the row predicate does not count toward the caller's filter limits.
    /// </summary>
    [SkippableFact]
    public async Task C4_CallerFilterOf995Chars_PlusRule_Answers200()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        string Filter(string pad) => Attr("branchCode", "in", new[] { "BR-001", "BR-002", "BR-003", pad });
        var overhead = ComposeFilter(Flow, Filter(""))!.Length;
        var filter = Filter(new string('P', 995 - overhead));
        Assert.Equal(995, ComposeFilter(Flow, filter)!.Length);

        AssertIds(p, await ListIdsAsync(Flow, BranchStaff("BR-001"), filter), "C-4", p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// C-5: Definition B (<c>rs-b</c>), alice without roles (own instances <c>{r1, r3}</c>) filtering
    /// <c>createdBy eq carol</c> ⇒ none (intersection). Control: <c>createdBy eq alice</c> ⇒ <c>{r1, r3}</c>.
    /// </summary>
    [SkippableFact]
    public async Task C5_DefinitionB_CallerFilterOnAnotherStarter_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        const string flow = "rs-b";
        var p = await SeedAsync(flow);
        var alice = new Caller(Sub: "alice", ActSub: "alice");

        AssertIds(p, await ListIdsAsync(flow, alice, """{"createdBy":{"eq":"carol"}}"""), "C-5 carol");
        AssertIds(p, await ListIdsAsync(flow, alice, """{"createdBy":{"eq":"alice"}}"""), "C-5 alice", p.R1, p.R3);
    }

    /// <summary>
    /// C-6: <c>pageSize=1</c> as BR-001: three pages carry one visible row each (together own), the <c>next</c> link
    /// is present while a visible row remains and empty on the last page; page 4 is empty.
    /// </summary>
    [SkippableFact]
    public async Task C6_Paging_NextLinkOnlyWhileAVisibleRowRemains()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var (status, body) = await ListRawAsync(Flow, BranchStaff("BR-001"), extraQuery: $"page={page}", pageSize: 1);
            Assert.True(status == HttpStatusCode.OK, $"page {page} answered {(int)status}: {body}");
            var root = JsonDocument.Parse(body).RootElement;
            var items = root.GetProperty("items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("id").GetString()!)).ToList();
            var next = root.GetProperty("links").TryGetProperty("next", out var link) ? link.GetString() : null;
            seen.AddRange(items);

            Assert.True(items.Count == (page <= 3 ? 1 : 0), $"page {page}: {items.Count} items: {body}");
            Assert.True(page < 3 ? !string.IsNullOrEmpty(next) : string.IsNullOrEmpty(next),
                $"page {page}: next link '{next}' (a visible row remains only before page 3)");
        }

        AssertIds(p, seen, "C-6 pages together", p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// U-route: no caller filter at all (string route, every <c>rs-a</c> row of every run): paging through the whole
    /// visible set as BR-001, every row's latest <c>branchCode</c> is BR-001, this run's own rows are all there and
    /// none of its foreign rows (r3, r4, r6, r7) appears on any page.
    /// </summary>
    [SkippableFact]
    public async Task URoute_NoCallerFilter_EveryRowOnEveryPageIsOwnBranch()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        var seen = new HashSet<Guid>();
        for (var page = 1; ; page++)
        {
            var (status, body) = await ListRawAsync(Flow, BranchStaff("BR-001"), RawNoTestId, $"page={page}");
            Assert.True(status == HttpStatusCode.OK, $"page {page} answered {(int)status}: {body}");
            var root = JsonDocument.Parse(body).RootElement;
            var items = root.GetProperty("items").EnumerateArray().ToList();
            foreach (var item in items)
            {
                var id = Guid.Parse(item.GetProperty("id").GetString()!);
                var branch = item.GetProperty("attributes").TryGetProperty("branchCode", out var b) ? b.GetString() : null;
                Assert.True(branch == "BR-001", $"page {page}: {Names(p, [id])} has branchCode '{branch}'");
                seen.Add(id);
            }

            var next = root.GetProperty("links").TryGetProperty("next", out var link) ? link.GetString() : null;
            if (items.Count == 0 || string.IsNullOrEmpty(next)) break;
            Assert.True(page < 1000, "more than 1000 pages of BR-001 rows");
        }

        Assert.Subset(seen, new HashSet<Guid> { p.R1, p.R2, p.R5 });
        Assert.Empty(seen.Intersect([p.R3, p.R4, p.R6, p.R7]));
    }

    // ── grouping and aggregation ─────────────────────────────────────────────

    /// <summary>G-1: <c>groupBy branchCode</c> via the envelope as BR-001 ⇒ exactly one group, BR-001, count 3.</summary>
    [SkippableFact]
    public async Task G1_GroupByEnvelope_OneGroupOwnBranchCount3()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await SeedAsync(Flow);

        var groups = await GroupsAsync(BranchStaff("BR-001"), GroupByBranch);
        Assert.Equal([("BR-001", 3L)], groups.Select(g => (g.Name, g.Count)));
    }

    /// <summary>
    /// G-2 (task half): the same envelope through a GetInstances task (<c>rs-reader</c> <c>read-list</c>, caller
    /// <c>branch-staff</c>, <c>x-branch-code</c> mapped) ⇒ the same groups as G-1.
    /// </summary>
    [SkippableFact]
    public async Task G2_GroupByThroughGetInstancesTask_EqualsG1()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await SeedAsync(Flow);
        var caller = BranchStaff("BR-001");
        var direct = await GroupsAsync(caller, GroupByBranch);

        var reader = await ReadThroughReaderAsync(caller, "read-list", new
        {
            targetFlow = Flow,
            filter = GroupEnvelope(Flow, GroupByBranch),
            headers = new Dictionary<string, string> { ["x-branch-code"] = "BR-001" },
        });
        Assert.True(reader.GetProperty("isSuccess").ValueKind == JsonValueKind.True, $"task result: {reader}");
        var viaTask = ParseGroups(reader.GetProperty("response"));

        Assert.Equal(direct.Select(g => (g.Name, g.Count)), viaTask.Select(g => (g.Name, g.Count)));
        Assert.Equal([("BR-001", 3L)], viaTask.Select(g => (g.Name, g.Count)));
    }

    /// <summary>
    /// G-2 (string-route half): <c>groupBy</c> as its own query parameter next to a plain filter
    /// (<c>GetInstanceListInput.GroupBy</c> reads it from the query) skips the envelope parse, so the grouping runs
    /// through the repository's string overload ⇒ the same groups as G-1.
    /// </summary>
    [SkippableFact]
    public async Task G2_GroupByThroughStringRoute_EqualsG1()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await SeedAsync(Flow);
        var caller = BranchStaff("BR-001");
        var direct = await GroupsAsync(caller, GroupByBranch);

        var (status, body) = await ListRawAsync(Flow, caller, extraQuery: "groupBy=" + Uri.EscapeDataString(GroupByBranch));
        Assert.True(status == HttpStatusCode.OK, $"answered {(int)status}: {body}");
        var viaString = ParseGroups(JsonDocument.Parse(body).RootElement);

        Assert.Equal(direct.Select(g => (g.Name, g.Count)), viaString.Select(g => (g.Name, g.Count)));
        Assert.Equal([("BR-001", 3L)], viaString.Select(g => (g.Name, g.Count)));
    }

    /// <summary>
    /// G-3: count + sum(amount) per branch: <c>hq-auditor</c> totals cover all seven rows (count 7, sum 2210);
    /// BR-001 totals cover its visible rows only (one group, count 3, sum 100 + 200 + 500 = 800).
    /// </summary>
    [SkippableFact]
    public async Task G3_Aggregations_CoverVisibleRowsOnly()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await SeedAsync(Flow);

        var hq = await GroupsAsync(Hq(), GroupByBranchWithSum);
        Assert.Equal(7L, hq.Sum(g => g.Count));
        Assert.Equal(2210m, hq.Sum(g => g.Sum));

        var own = await GroupsAsync(BranchStaff("BR-001"), GroupByBranchWithSum);
        Assert.Equal([("BR-001", 3L, 800m)], own.Select(g => (g.Name, g.Count, g.Sum)));
    }

    /// <summary>G-4: role-less caller (deny by default) ⇒ 200 with no groups.</summary>
    [SkippableFact]
    public async Task G4_Roleless_Returns200WithEmptyGroups()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        await SeedAsync(Flow);

        var groups = await GroupsAsync(new Caller(Sub: "alice", ActSub: "alice"), GroupByBranch);
        Assert.Empty(groups);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Group(string Name, long Count, decimal Sum);

    private async Task<IReadOnlyList<Group>> GroupsAsync(Caller caller, string groupBy)
    {
        var (status, body) = await ListGroupedAsync(Flow, caller, groupBy);
        Assert.True(status == HttpStatusCode.OK, $"grouped list answered {(int)status}: {body}");
        return ParseGroups(JsonDocument.Parse(body).RootElement);
    }

    /// <summary>Groups of a list body; an item without <c>count</c> is an instance, which fails the row.</summary>
    private static IReadOnlyList<Group> ParseGroups(JsonElement listBody)
    {
        var groups = new List<Group>();
        foreach (var item in listBody.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.TryGetProperty("count", out var count), $"expected group items, got {item}");
            var sum = item.TryGetProperty("sum", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDecimal() : 0m;
            groups.Add(new Group(item.GetProperty("name").GetString() ?? "", count.GetInt64(), sum));
        }

        return groups.OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
    }
}
