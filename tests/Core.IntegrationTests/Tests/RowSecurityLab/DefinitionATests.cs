using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Definition A (<c>rs-a</c>): <c>branch-staff → branchCode eq $.context.Headers.x-branch-code</c>,
/// <c>hq-auditor</c> unfiltered, no identity rule, no <c>default</c> (deny by default, K1). Rows A-1…A-8 are the
/// first eight lines of the Definition A outcome table (docs/domain/row-security.md); "role resolution failed" is a
/// morph-idm row (11G). Every row asserts the list AND the GET of all seven ids.
/// <para>
/// "Own branch" for <c>x-branch-code: BR-001</c> is evaluated on latest data: <c>{r1, r2, r5}</c> (R-F2).
/// Default-provider run only: under morph-idm the role set comes from the IDM, not from the <c>role</c> header.
/// </para>
/// </summary>
public class DefinitionATests : RowSecurityLabTestBase
{
    private const string Flow = "rs-a";
    private const string SkipReason = "D run: the role header is the role set only under the default caller-role provider";

    public DefinitionATests(VNextTestEnvironment environment) : base(environment) { }

    private static Dictionary<string, string> Branch(string code) => new() { ["x-branch-code"] = code };

    /// <summary>A-1: no credential (no sub, no act_sub, no role) ⇒ none.</summary>
    [SkippableFact]
    public async Task A1_NoCredential_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller());
    }

    /// <summary>A-2: identity alice, no roles ⇒ none (no static rule matches, no identity rule declared).</summary>
    [SkippableFact]
    public async Task A2_IdentityWithoutRoles_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice"));
    }

    /// <summary>A-3: identity alice, <c>[menu.view]</c> ⇒ none (an unrelated role matches no rule).</summary>
    [SkippableFact]
    public async Task A3_UnrelatedRole_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice", Role: "menu.view"));
    }

    /// <summary>A-4: <c>[branch-staff]</c> + <c>x-branch-code: BR-001</c> ⇒ own branch <c>{r1, r2, r5}</c>.</summary>
    [SkippableFact]
    public async Task A4_BranchStaffWithHeader_SeesOwnBranch()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch("BR-001")),
            p.R1, p.R2, p.R5);
    }

    /// <summary>A-5: <c>[branch-staff]</c>, rule header missing ⇒ none (a missing parameter contributes nothing).</summary>
    [SkippableFact]
    public async Task A5_BranchStaffWithoutHeader_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff"));
    }

    /// <summary>A-6: <c>[branch-staff, menu.view, report.read]</c> + BR-001 ⇒ own branch; extra roles add nothing.</summary>
    [SkippableFact]
    public async Task A6_BranchStaffWithExtraRoles_SeesOwnBranchOnly()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff,menu.view,report.read",
                Headers: Branch("BR-001")),
            p.R1, p.R2, p.R5);
    }

    /// <summary>A-7: <c>[branch-staff, hq-auditor]</c> + BR-001 ⇒ all (OR of the matched rules; hq has no filter).</summary>
    [SkippableFact]
    public async Task A7_BranchStaffAndHqAuditor_SeesAll()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff,hq-auditor", Headers: Branch("BR-001")),
            [.. p.All]);
    }

    /// <summary>A-8: <c>[hq-auditor]</c> (explicit unfiltered rule) ⇒ all.</summary>
    [SkippableFact]
    public async Task A8_HqAuditor_SeesAll()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice", Role: HqAuditor), [.. p.All]);
    }
}
