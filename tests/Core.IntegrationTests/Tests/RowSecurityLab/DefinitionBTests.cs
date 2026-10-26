using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RowSecurityLab;

/// <summary>
/// Definition B (<c>rs-b</c>): Definition A plus <c>{ "roles": ["$InstanceStarter"] }</c> (row predicate
/// <c>createdBy eq &lt;caller act_sub&gt;</c>). Rows B-1…B-6 are the first six lines of the Definition B outcome
/// table (docs/domain/row-security.md); "role resolution failed" is a morph-idm row (11G).
/// <para>
/// alice started r1 and r3 ⇒ "own instances" = <c>{r1, r3}</c>. "Own branch" BR-001 on latest data =
/// <c>{r1, r2, r5}</c> (R-F2), BR-002 = <c>{r3, r4}</c> (<c>rs-b</c> has no status filter, closed r4 counts).
/// Default-provider run only.
/// </para>
/// </summary>
public class DefinitionBTests : RowSecurityLabTestBase
{
    private const string Flow = "rs-b";
    private const string SkipReason = "D run: the role header is the role set only under the default caller-role provider";

    public DefinitionBTests(VNextTestEnvironment environment) : base(environment) { }

    private static Dictionary<string, string> Branch(string code) => new() { ["x-branch-code"] = code };

    /// <summary>B-1: no credential ⇒ none (no role, and no identity for the identity rule to bind).</summary>
    [SkippableFact]
    public async Task B1_NoCredential_SeesNothing()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller());
    }

    /// <summary>B-2: identity alice, no roles ⇒ own instances <c>{r1, r3}</c>.</summary>
    [SkippableFact]
    public async Task B2_IdentityWithoutRoles_SeesOwnInstances()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice"), p.R1, p.R3);
    }

    /// <summary>B-3: identity alice, <c>[menu.view]</c> ⇒ own instances <c>{r1, r3}</c>.</summary>
    [SkippableFact]
    public async Task B3_IdentityWithUnrelatedRole_SeesOwnInstances()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice", Role: "menu.view"), p.R1, p.R3);
    }

    /// <summary>
    /// B-4: identity alice, <c>[branch-staff]</c>, <c>x-branch-code: BR-002</c> ⇒ own branch <c>{r3, r4}</c> ∪ own
    /// instances <c>{r1, r3}</c> = <c>{r1, r3, r4}</c>.
    /// </summary>
    [SkippableFact]
    public async Task B4_BranchStaffWithHeader_SeesOwnBranchUnionOwnInstances()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p,
            new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff", Headers: Branch("BR-002")),
            p.R1, p.R3, p.R4);
    }

    /// <summary>B-5: identity alice, <c>[branch-staff]</c>, header missing ⇒ own instances <c>{r1, r3}</c>.</summary>
    [SkippableFact]
    public async Task B5_BranchStaffWithoutHeader_SeesOwnInstances()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Sub: "alice", ActSub: "alice", Role: "branch-staff"), p.R1, p.R3);
    }

    /// <summary>B-6: no identity, <c>[branch-staff]</c> + BR-001 ⇒ own branch <c>{r1, r2, r5}</c>; no add-on.</summary>
    [SkippableFact]
    public async Task B6_NoIdentityBranchStaff_SeesOwnBranch()
    {
        Skip.IfNot(ProviderIs("default"), SkipReason);
        var p = await SeedAsync(Flow);

        await AssertVisibleAsync(Flow, p, new Caller(Role: "branch-staff", Headers: Branch("BR-001")),
            p.R1, p.R2, p.R5);
    }
}
