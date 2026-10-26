using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Parameter rows P-1…P-25, P-27…P-30 on <c>rs-params</c> (1.0.1). Each caller holds exactly ONE <c>rs-p-*</c> role,
/// so it matches exactly one rule: a "none" result proves that a failed parameter widened nothing (there is no
/// <c>default</c>, K1). P-26 is a publish rejection (Task 11D).
/// <para>
/// Sets follow the population on LATEST data: "own (BR-001)" = <c>{r1, r2, r5}</c> (R-F2). Every row asserts the
/// list AND the GET of all seven ids (<see cref="RowSecurityLabTestBase.AssertVisibleAsync"/>), so "none" is also
/// "every GET answers 404" — never 400 / 500. Default-provider run only (the role comes from the <c>role</c> header).
/// </para>
/// </summary>
public class ParameterTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-params";

    public ParameterTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>A caller holding exactly <paramref name="role"/>, identity alice, plus the rule's inputs.</summary>
    private static Caller P(string role, Dictionary<string, string>? headers = null, string? query = null) =>
        new(Sub: "alice", ActSub: "alice", Role: role, Headers: headers, Query: query);

    private static Dictionary<string, string> H(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value);

    // ── header eq ────────────────────────────────────────────────────────────

    /// <summary>P-1: <c>branchCode eq H.x-branch-code</c>, header absent ⇒ none.</summary>
    [SkippableFact]
    public async Task P01_HeaderAbsent_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-header-eq"));
    }

    /// <summary>P-2: same rule, header empty or blank ⇒ none (an empty parameter contributes nothing).</summary>
    [SkippableFact]
    public async Task P02_HeaderEmptyOrBlank_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-header-eq", H(("x-branch-code", ""))));
        await AssertVisibleAsync(Flow, p, P("rs-p-header-eq", H(("x-branch-code", "   "))));
    }

    // ── query parameters (client-controlled) ─────────────────────────────────

    /// <summary>
    /// P-3: <c>branchCode eq Q.branch</c> with <c>?branch=BR-001&amp;branch=BR-002</c> ⇒ none: a scalar operator fed a
    /// multi-valued source is ambiguous (there is no "first value"); first-value binding would give own.
    /// </summary>
    [SkippableFact]
    public async Task P03_QueryRepeatedOnScalarOperator_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: "branch=BR-001&branch=BR-002"));
    }

    /// <summary>P-4: positive control for P-3, <c>?branch=BR-001</c> ⇒ own <c>{r1, r2, r5}</c> (R-F2).</summary>
    [SkippableFact]
    public async Task P04_QuerySingleValue_SeesOwnBranch()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: "branch=BR-001"), p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// P-10: <c>branchCode in {$param: Q.br}</c> (no split) binds a repeated query key ELEMENT-WISE. Discriminating
    /// cases (first-value binding would give a different set): <c>?br=BR-003&amp;br=BR-002</c> ⇒ <c>{r3, r4}</c>
    /// (first value BR-003 matches nothing on latest data ⇒ first-value binding gives {}), and
    /// <c>?br=BR-002&amp;br=BR-001</c> ⇒ <c>{r1…r5}</c> (first value alone gives {r3, r4}). Control, the brief's
    /// case: <c>?br=BR-001&amp;br=BR-003</c> ⇒ <c>{r1, r2, r5}</c> on LATEST data (r5 started BR-003, is BR-001 now).
    /// </summary>
    [SkippableFact]
    public async Task P10_QueryRepeatedOnIn_IsElementWise_OnLatestData()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-query-br", query: "br=BR-003&br=BR-002"), p.R3, p.R4);
        await AssertVisibleAsync(Flow, p, P("rs-p-query-br", query: "br=BR-002&br=BR-001"),
            p.R1, p.R2, p.R3, p.R4, p.R5);
        await AssertVisibleAsync(Flow, p, P("rs-p-query-br", query: "br=BR-001&br=BR-003"), p.R1, p.R2, p.R5);
    }

    // ── arrays: in (Form A / Form C) ─────────────────────────────────────────

    /// <summary>P-5: <c>in [H.x-b1, H.x-b2]</c>, x-b1 = BR-001, x-b2 absent ⇒ the unresolved element is dropped ⇒ own.</summary>
    [SkippableFact]
    public async Task P05_InElements_OneUnresolved_KeepsTheResolvedOne()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-elements", H(("x-b1", "BR-001"))), p.R1, p.R2, p.R5);
    }

    /// <summary>P-6: same rule, both absent ⇒ no element left ⇒ none.</summary>
    [SkippableFact]
    public async Task P06_InElements_NoneResolved_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-elements"));
    }

    /// <summary>P-7: <c>in [H.x-b1, "BR-999"]</c>, x-b1 absent ⇒ the literal stays ⇒ <c>{r6}</c>.</summary>
    [SkippableFact]
    public async Task P07_InWithLiteral_ParameterAbsent_KeepsTheLiteral()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-literal"), p.R6);
    }

    /// <summary>
    /// P-8: <c>in {$param: H.x-branches, split: ","}</c> with <c>" BR-001 , BR-002,,"</c> ⇒ trimmed, empties dropped
    /// ⇒ <c>{BR-001, BR-002}</c> ⇒ <c>{r1, r2, r3, r4, r5}</c> (r4 is closed; <c>rs-params</c> has no status filter).
    /// </summary>
    [SkippableFact]
    public async Task P08_InSplit_TrimsAndDropsEmptyElements()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", " BR-001 , BR-002,,"))),
            p.R1, p.R2, p.R3, p.R4, p.R5);
    }

    /// <summary>P-9: same rule, <c>",,"</c> ⇒ no element left ⇒ none.</summary>
    [SkippableFact]
    public async Task P09_InSplit_OnlySeparators_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", ",,"))));
    }

    /// <summary>
    /// P-11: <c>in {$param: H.x-branches}</c> (no split) with the single value <c>"BR-001,BR-003"</c> ⇒ no implicit
    /// split ⇒ one element that matches nothing ⇒ none. Positive control: <c>"BR-001"</c> ⇒ own.
    /// </summary>
    [SkippableFact]
    public async Task P11_InWithoutSplit_DoesNotSplitACommaValue()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-param", H(("x-branches", "BR-001,BR-003"))));
        await AssertVisibleAsync(Flow, p, P("rs-p-in-param", H(("x-branches", "BR-001"))), p.R1, p.R2, p.R5);
    }

    // ── widening operators: nin, ne ──────────────────────────────────────────

    /// <summary>
    /// P-12: <c>nin [H.x-excl-1, H.x-excl-2]</c> with BR-001, BR-002 ⇒ on latest data r5 is BR-001 (excluded) ⇒
    /// <c>{r6}</c>. r7 (no <c>branchCode</c>) is excluded by today's builder — that half is pinned by P-30.
    /// </summary>
    [SkippableFact]
    public async Task P12_NinElements_BothPresent_ExcludesBoth_OnLatestData()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            P("rs-p-nin-elements", H(("x-excl-1", "BR-001"), ("x-excl-2", "BR-002"))), p.R6);
    }

    /// <summary>P-13: same rule, x-excl-2 absent ⇒ a widening operator with a failed element drops the whole rule ⇒ none.</summary>
    [SkippableFact]
    public async Task P13_NinElements_OneAbsent_DropsTheRule()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-nin-elements", H(("x-excl-1", "BR-001"))));
    }

    /// <summary>P-14: <c>ne H.x-branch-code</c>, header absent ⇒ the whole rule is dropped ⇒ none (never "all").</summary>
    [SkippableFact]
    public async Task P14_Ne_ParameterAbsent_DropsTheRule()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-ne"));
    }

    // ── between, numbers ─────────────────────────────────────────────────────

    /// <summary>P-15: <c>amount between [H.x-min, H.x-max]</c> with 100, 500 ⇒ rows in range <c>{r1…r5}</c>.</summary>
    [SkippableFact]
    public async Task P15_Between_BothPresent_SeesRowsInRange()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-between", H(("x-min", "100"), ("x-max", "500"))),
            p.R1, p.R2, p.R3, p.R4, p.R5);
    }

    /// <summary>P-16: same rule, x-max absent ⇒ <c>between</c> without exactly two values ⇒ none.</summary>
    [SkippableFact]
    public async Task P16_Between_OneAbsent_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-between", H(("x-min", "100"))));
    }

    /// <summary>
    /// P-17: <c>between</c> drops for ARITY when an element parameter carries two values. <c>between</c> with
    /// <c>split</c> is a publish error (RS-E10), so the arity is reached as the Task 5B ruling does: <c>x-min</c> sent
    /// as two header LINES. HttpClient folds repeated headers into one line, so these requests go out over a raw
    /// HTTP/1.0 socket.
    /// <list type="number">
    /// <item>Precondition — the two lines arrive as two values, not one combined "a, b": the runtime keeps every
    /// header value separately (<c>HttpContextExtensions.GetRawRequestValues</c> copies <c>StringValues</c> into the
    /// raw carrier <c>RawHeaders</c>, Task 3, which the row planner reads). Proven on <c>rs-p-in-param</c>
    /// (<c>in {$param: H.x-branches}</c>, no split): lines BR-001 + BR-002 ⇒ <c>{r1…r5}</c>; a combined one-line
    /// value would be one element matching nothing (P-11) ⇒ {}.</item>
    /// <item>Two IDENTICAL lines <c>x-min: 100</c> + <c>x-min: 100</c> and <c>x-max: 500</c> ⇒ none, list and GET.
    /// Each line is a valid number, and by the precondition they are not combined, so no coercion failure is
    /// involved: three bounds ⇒ arity ≠ 2 ⇒ the rule contributes nothing.</item>
    /// <item>Control over the same socket: one <c>x-min</c> line ⇒ the rule works (rows in [100, 500]).</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task P17_Between_ElementWithTwoHeaderLines_DropsForArity()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var filter = Uri.EscapeDataString(ComposeFilter(Flow, null)!);
        var list = $"api/v1/{Domain}/workflows/{Flow}/instances?pageSize=100&filter={filter}";

        // 1. precondition: repeated header lines stay separate values
        (string, string)[] separate =
        [
            ("sub", "alice"), ("act_sub", "alice"), ("role", "rs-p-in-param"),
            ("x-branches", "BR-001"), ("x-branches", "BR-002"),
        ];
        Assert.Equal(new[] { p.R1, p.R2, p.R3, p.R4, p.R5 }.ToHashSet(), await RawListIdsAsync(list, separate));

        // 2. arity: two identical, individually valid lines + x-max
        (string, string)[] arity =
        [
            ("sub", "alice"), ("act_sub", "alice"), ("role", "rs-p-between"),
            ("x-min", "100"), ("x-min", "100"), ("x-max", "500"),
        ];
        Assert.Empty(await RawListIdsAsync(list, arity));
        foreach (var id in p.All)
        {
            var (getStatus, getBody) = await RawGetAsync($"api/v1/{Domain}/workflows/{Flow}/instances/{id}", arity);
            Assert.True(getStatus == 404, $"raw GET {Names(p, [id])} answered {getStatus}: {getBody}");
        }

        // 3. control: one x-min line ⇒ rows in [100, 500]
        (string, string)[] control = [.. arity.Where(h => h.Item1 != "x-min"), ("x-min", "100")];
        Assert.Equal(new[] { p.R1, p.R2, p.R3, p.R4, p.R5 }.ToHashSet(), await RawListIdsAsync(list, control));
    }

    private async Task<HashSet<Guid>> RawListIdsAsync(string relativeUrl, IEnumerable<(string Name, string Value)> headers)
    {
        var (status, body) = await RawGetAsync(relativeUrl, headers);
        Assert.True(status == 200, $"raw list answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => Guid.Parse(i.GetProperty("id").GetString()!)).ToHashSet();
    }

    /// <summary>P-18: <c>amount ge H.x-min</c> with <c>"50"</c> ⇒ numeric comparison (coerced): everything but r6 (10).</summary>
    [SkippableFact]
    public async Task P18_GeNumber_ComparesNumerically()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-ge-number", H(("x-min", "50"))),
            p.R1, p.R2, p.R3, p.R4, p.R5, p.R7);
    }

    /// <summary>P-19: same rule, <c>"abc"</c> ⇒ coercion fails, the rule is dropped ⇒ 200 with none, never 400/500.</summary>
    [SkippableFact]
    public async Task P19_GeNumber_NotANumber_Returns200WithNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-ge-number", H(("x-min", "abc"))));
    }

    // ── CurrentUser, shorthand, header case ──────────────────────────────────

    /// <summary>P-21a: <c>owner eq CU.UserName</c>, <c>sub: alice</c> ⇒ alice's rows <c>{r1, r3}</c>.</summary>
    [SkippableFact]
    public async Task P21a_CurrentUserName_SeesOwnRows()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-username"), p.R1, p.R3);
    }

    /// <summary>
    /// P-21b: <c>positionCode eq CU.Position</c>, no <c>position</c> header ⇒ none. Control: <c>position: POS-A</c> ⇒
    /// <c>{r1, r3, r7}</c>.
    /// </summary>
    [SkippableFact]
    public async Task P21b_CurrentUserPosition_AbsentSeesNothing_PresentSeesItsRows()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-position"));
        await AssertVisibleAsync(Flow, p, P("rs-p-position", H(("position", "POS-A"))), p.R1, p.R3, p.R7);
    }

    /// <summary>
    /// P-22: <c>createdBy eq $InstanceStarter</c> inside a STATIC-role rule (<c>rs-p-createdby-starter</c>) with
    /// <c>act_sub: alice</c> ⇒ the rows alice started <c>{r1, r3}</c>.
    /// </summary>
    [SkippableFact]
    public async Task P22_InstanceStarterShorthandInStaticRule_SeesRowsTheActorStarted()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(ActSub: "alice", Role: "rs-p-createdby-starter"), p.R1, p.R3);
    }

    /// <summary>P-23: the header name is case-insensitive: <c>X-Branch-Code: BR-001</c> ⇒ own.</summary>
    [SkippableFact]
    public async Task P23_HeaderNameCase_IsIgnored()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-header-eq", H(("X-Branch-Code", "BR-001"))), p.R1, p.R2, p.R5);
    }

    // ── limits and hostile values ────────────────────────────────────────────

    /// <summary>
    /// P-24: caps (<c>MaxElements</c> 100, <c>MaxElementLength</c> 1000) on <c>in-split</c>, each with BR-001 among
    /// the elements so "works" is visible as own: 100 elements ⇒ own, 101 ⇒ none; an element of 1000 chars ⇒ own,
    /// 1001 ⇒ none.
    /// </summary>
    [SkippableFact]
    public async Task P24_Caps_ElementCountAndLength()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        static string Elements(int count) =>
            string.Join(",", new[] { "BR-001" }.Concat(Enumerable.Range(1, count - 1).Select(i => $"BR-F{i:D3}")));

        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", Elements(100)))), p.R1, p.R2, p.R5);
        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", Elements(101)))));
        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", "BR-001," + new string('X', 1000)))),
            p.R1, p.R2, p.R5);
        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", "BR-001," + new string('X', 1001)))));
    }

    /// <summary>
    /// P-25a: injection payloads as the rule value ⇒ none and no 5xx (list 200, every GET 404): header payloads on
    /// <c>rs-p-header-eq</c>, and the SQL payload as a query value on <c>rs-p-query-branch</c>.
    /// </summary>
    [SkippableFact]
    public async Task P25a_InjectionPayloads_SeeNothing_No5xx()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        foreach (var payload in new[] { "' OR '1'='1", "\"},{\"or\":[", "%" })
            await AssertVisibleAsync(Flow, p, P("rs-p-header-eq", H(("x-branch-code", payload))));

        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: "branch=" + Uri.EscapeDataString("' OR '1'='1")));
    }

    /// <summary>
    /// P-25b: the NUL payload (<c>\u0000</c>) ⇒ none and no 5xx. NUL cannot travel in a header, so it goes as
    /// <c>?branch=%00</c> on <c>rs-p-query-branch</c>. Found red in Task 11C (the value reached PostgreSQL
    /// and answered 500, 22P05); fixed in vnext 5d6ac9e5: the planner drops a rule whose parameter value contains
    /// NUL (coercion). A caller's own filter with <c>\u0000</c> still answers 500 on any flow (filter engine).
    /// </summary>
    [SkippableFact]
    public async Task P25b_NulPayload_SeesNothing_No5xx()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: "branch=%00"));
    }

    // ── compound and polarity ────────────────────────────────────────────────

    /// <summary>
    /// P-27: <c>branchCode eq H.x-branch-code AND (region eq H.x-region OR amount ge H.x-min)</c> with x-min absent ⇒
    /// one unresolved leaf collapses the whole rule ⇒ none (although BR-001 AND north alone would match). Control:
    /// all three present ⇒ own.
    /// </summary>
    [SkippableFact]
    public async Task P27_Compound_OneLeafUnresolved_CollapsesTheWholeRule()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-and-or", H(("x-branch-code", "BR-001"), ("x-region", "north"))));
        await AssertVisibleAsync(Flow, p,
            P("rs-p-and-or", H(("x-branch-code", "BR-001"), ("x-region", "north"), ("x-min", "0"))),
            p.R1, p.R2, p.R5);
    }

    /// <summary>
    /// P-28: <c>not { in [H.x-b1, H.x-b2] }</c> with x-b2 absent ⇒ a failure under <c>not</c> drops the whole rule
    /// (dropping the element would widen) ⇒ none.
    /// </summary>
    [SkippableFact]
    public async Task P28_NotIn_OneElementAbsent_DropsTheRule()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-not-in", H(("x-b1", "BR-001"))));
    }

    /// <summary>P-29: <c>not { branchCode eq H.x-branch-code }</c>, header absent ⇒ none, never "all".</summary>
    [SkippableFact]
    public async Task P29_NotEq_ParameterAbsent_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-not-eq"));
    }

    /// <summary>
    /// P-30 [pin]: against r7 (no <c>branchCode</c>) the current builder is not symmetric — <c>nin [BR-001, BR-002]</c>
    /// EXCLUDES r7 (<c>{r6}</c>), <c>not { eq BR-001 }</c> INCLUDES it (<c>{r3, r4, r6, r7}</c>, containment).
    /// Publish warns RS-W5 for both shapes on a field that is not <c>required</c>. Asserts today's behaviour.
    /// </summary>
    [SkippableFact]
    public async Task P30_Pinned_NinExcludesMissingField_NotEqIncludesIt()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            P("rs-p-nin-elements", H(("x-excl-1", "BR-001"), ("x-excl-2", "BR-002"))), p.R6);
        await AssertVisibleAsync(Flow, p, P("rs-p-not-eq", H(("x-branch-code", "BR-001"))),
            p.R3, p.R4, p.R6, p.R7);
    }

    // ── raw socket ───────────────────────────────────────────────────────────

    /// <summary>
    /// One HTTP/1.0 GET over a plain socket, each header written as its own line (repeated names stay repeated).
    /// HTTP/1.0 makes the server close the connection and answer without chunked encoding.
    /// </summary>
    private async Task<(int Status, string Body)> RawGetAsync(string relativeUrl, IEnumerable<(string Name, string Value)> headers)
    {
        var target = new Uri(OrchestratorUri, relativeUrl);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(target.Host, target.Port);
        await using var stream = tcp.GetStream();

        var request = new StringBuilder()
            .Append($"GET {target.PathAndQuery} HTTP/1.0\r\n")
            .Append($"Host: {target.Authority}\r\n")
            .Append($"x-request-id: {Guid.NewGuid()}\r\n")
            .Append("x-device-id: integration-test-device\r\n")
            .Append("x-token-id: integration-test-token\r\n");
        foreach (var (name, value) in headers) request.Append($"{name}: {value}\r\n");
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var response = Encoding.UTF8.GetString(buffer.ToArray());
        var lineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
        Assert.True(lineEnd > 0 && response.StartsWith("HTTP/", StringComparison.Ordinal),
            $"raw GET {target.PathAndQuery}: no HTTP status line in the answer ({response.Length} bytes): '{response}'");
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var statusLine = response[..lineEnd];
        return (int.Parse(statusLine.Split(' ')[1]), split < 0 ? string.Empty : response[(split + 4)..]);
    }
}
