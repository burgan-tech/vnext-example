using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Canary rows (C-3): the parameter rows P-1…P-25 again, with every client-controlled parameter value carrying
/// <see cref="Canary"/>. Two things are proven: (1) a value that matches no row never widens a rule — each canary-only
/// caller sees nothing, list AND GET of all seven ids, and a canary next to a valid value keeps exactly the valid
/// value's rows; (2) the value never leaves the request — Task 12 searches the logs (<c>body</c> and every attribute)
/// and every span attribute for <see cref="Canary"/> over the run window and expects 0 hits (constraint: "never
/// log/tag parameter values").
/// <para>
/// Sources covered: headers (eq, in elements, in split, in without split, nin, ne, between, ge number, not, and/or),
/// <c>CurrentUser.Position</c> (the <c>position</c> header), query parameters (single, repeated on a scalar operator,
/// repeated element-wise on <c>in</c>), repeated header LINES (raw socket), and the Definition A rule on <c>rs-a</c>.
/// Identity headers (<c>sub</c>, <c>act_sub</c>) are left out on purpose: the runtime records the caller's identity
/// outside row security, so a hit there would not say anything about parameter values.
/// </para>
/// </summary>
public class CanaryTests : RowSecurityLabTestBase
{
    /// <summary>The marker searched for in logs and traces after the run.</summary>
    public const string Canary = "CANARY-7f3a";

    private const string Flow = "rs-params";

    public CanaryTests(VNextTestEnvironment environment) : base(environment) { }

    private static Caller P(string role, Dictionary<string, string>? headers = null, string? query = null) =>
        new(Sub: "alice", ActSub: "alice", Role: role, Headers: headers, Query: query);

    private static Dictionary<string, string> H(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value);

    /// <summary>Header-sourced rules (P-1, P-2, P-5…P-9, P-11…P-19, P-21b, P-23, P-24, P-27…P-29): canary only ⇒ none.</summary>
    [SkippableFact]
    public async Task C3a_CanaryInEveryHeaderSource_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        (string Role, Dictionary<string, string> Headers)[] callers =
        [
            ("rs-p-header-eq", H(("x-branch-code", Canary))),
            ("rs-p-header-eq", H(("X-Branch-Code", " " + Canary + " "))),
            ("rs-p-in-elements", H(("x-b1", Canary), ("x-b2", Canary))),
            ("rs-p-in-split", H(("x-branches", $" {Canary} ,{Canary}-2,,"))),
            ("rs-p-in-param", H(("x-branches", $"{Canary},BR-001"))),
            ("rs-p-nin-elements", H(("x-excl-1", Canary))),
            ("rs-p-ne", H(("x-other", Canary))), // its own header absent, the canary in an unrelated one
            ("rs-p-between", H(("x-min", Canary), ("x-max", Canary))),
            ("rs-p-ge-number", H(("x-min", Canary))),
            ("rs-p-not-in", H(("x-b1", Canary))),
            ("rs-p-and-or", H(("x-branch-code", Canary), ("x-region", Canary), ("x-min", Canary))),
            ("rs-p-region", H(("x-region", Canary))),
            ("rs-p-position", H(("position", Canary))),
            ("rs-p-in-split", H(("x-branches", string.Join(",", Enumerable.Range(0, 101).Select(i => $"{Canary}-{i}"))))),
            ("rs-p-in-split", H(("x-branches", Canary + new string('X', 1001)))),
        ];

        foreach (var (role, headers) in callers)
            await AssertVisibleAsync(Flow, p, P(role, headers));
    }

    /// <summary>Query-sourced rules (P-3, P-4, P-10, P-25a/b): canary only ⇒ none; next to BR-001 ⇒ own, never more.</summary>
    [SkippableFact]
    public async Task C3b_CanaryInQueryParameters_NeverWidens()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var c = Uri.EscapeDataString(Canary);

        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: $"branch={c}"));
        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: $"branch={c}&branch=BR-001"));
        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: $"branch={Uri.EscapeDataString(Canary + "' OR '1'='1")}"));
        await AssertVisibleAsync(Flow, p, P("rs-p-query-branch", query: $"branch={c}%00"));
        await AssertVisibleAsync(Flow, p, P("rs-p-query-br", query: $"br={c}&br={c}-2"));
        await AssertVisibleAsync(Flow, p, P("rs-p-query-br", query: $"br={c}&br=BR-001"), p.R1, p.R2, p.R5);
    }

    /// <summary>A canary next to a valid value keeps exactly the valid value's rows (P-5, P-7 shapes).</summary>
    [SkippableFact]
    public async Task C3c_CanaryBesideAValidValue_KeepsOnlyTheValidRows()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, P("rs-p-in-elements", H(("x-b1", "BR-001"), ("x-b2", Canary))), p.R1, p.R2, p.R5);
        await AssertVisibleAsync(Flow, p, P("rs-p-in-literal", H(("x-b1", Canary))), p.R6);
        await AssertVisibleAsync(Flow, p, P("rs-p-in-split", H(("x-branches", $"BR-001,{Canary}"))), p.R1, p.R2, p.R5);
        // nin with both elements resolved excludes only rows whose branch is a canary ⇒ every row with a branchCode
        // (r7 has none and is excluded by today's builder, P-30)
        await AssertVisibleAsync(Flow, p, P("rs-p-nin-elements", H(("x-excl-1", Canary), ("x-excl-2", Canary))),
            p.R1, p.R2, p.R3, p.R4, p.R5, p.R6);
    }

    /// <summary>Repeated header LINES (raw socket, P-17 shape): canary lines ⇒ none; canary + BR-001 ⇒ own.</summary>
    [SkippableFact]
    public async Task C3d_CanaryInRepeatedHeaderLines_NeverWidens()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync(Flow);
        var filter = Uri.EscapeDataString(ComposeFilter(Flow, null)!);
        var list = $"api/v1/{Domain}/workflows/{Flow}/instances?pageSize=100&filter={filter}";

        (string, string)[] canaryOnly =
        [
            ("sub", "alice"), ("act_sub", "alice"), ("role", "rs-p-in-param"),
            ("x-branches", Canary), ("x-branches", Canary + "-2"),
        ];
        Assert.Empty(await RawListIdsAsync(list, canaryOnly));
        foreach (var id in p.All)
        {
            var (status, body) = await RawGetAsync($"api/v1/{Domain}/workflows/{Flow}/instances/{id}", canaryOnly);
            Assert.True(status == 404, $"raw GET {Names(p, [id])} answered {status}: {body}");
        }

        (string, string)[] mixed =
        [
            ("sub", "alice"), ("act_sub", "alice"), ("role", "rs-p-in-param"),
            ("x-branches", Canary), ("x-branches", "BR-001"),
        ];
        Assert.Equal(new[] { p.R1, p.R2, p.R5 }.ToHashSet(), await RawListIdsAsync(list, mixed));

        (string, string)[] between =
        [
            ("sub", "alice"), ("act_sub", "alice"), ("role", "rs-p-between"),
            ("x-min", Canary), ("x-min", "100"), ("x-max", "500"),
        ];
        Assert.Empty(await RawListIdsAsync(list, between));
    }

    /// <summary>Definition A on <c>rs-a</c>: branch-staff with a canary branch header ⇒ none, list and GET.</summary>
    [SkippableFact]
    public async Task C3e_DefinitionA_CanaryBranchHeader_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), DefaultProviderOnly);
        var p = await SeedAsync("rs-a");

        await AssertVisibleAsync("rs-a", p,
            new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch(Canary)));
    }
}
