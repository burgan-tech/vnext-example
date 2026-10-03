using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// <c>authorize?queryRoles=true</c> on an instance with an active SubFlow is decided by the DEEPEST
/// ACTIVE LEAF alone — its stamped parent override, else its state's <c>queryRoles</c>, else its
/// workflow's — and each level's grants still resolve from the map its DIRECT parent stamped on it.
/// </summary>
/// <remarks>
/// <para><b>What changed (leaf-only rule, role-grant combinators, vnext <c>feature/role-grant-combinators</c>,
/// 2026-10-03).</b> This class used to be <c>ChainConjunctionTests</c> and pinned the opposite rule:
/// the polled instance's own <c>queryRoles</c> AND every level beneath it. That conjunction mirrored a
/// read path that gated twice (root, then leaf). The read surfaces stopped gating on
/// <c>queryRoles</c> on 2026-09-23 (the decision moved to the gateway), so the conjunction no longer
/// described anything the runtime does — and a SubFlow is part of its parent's process: while the
/// instance is inside it, the SubFlow's rules are the ones in force. A parent that wants to restrict
/// the leaf stamps it with <c>subFlow.overrides.states.&lt;state&gt;.queryRoles</c>.</para>
/// <para><b>Consequences pinned here.</b> (1) A role the root's own allowlist refuses is ALLOWED at
/// the root while the leaf admits it. (2) A role the root admits is still REFUSED when the leaf
/// does not — the leaf decides in both directions. (3) A leaf with no <c>queryRoles</c> anywhere
/// allows (empty set), whatever the root declares.</para>
/// <para>The chain is built so each role passes or fails at exactly one level, which is what lets a
/// wrong verdict name its own cause rather than just being red.</para>
/// </remarks>
public sealed class ChainLeafDecisionTests : AuthorizationChainLabTestBase
{
    public ChainLeafDecisionTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// <c>chain.reader</c> passes the ROOT's own allowlist and is absent from the mid's override of
    /// the leaf. The leaf decides, so the root's own allow does not admit it.
    /// </summary>
    /// <remarks>
    /// Was <c>ReaderPassesTheRootAndFailsTheChain</c> (expected <c>false</c> because of the
    /// conjunction). The expected verdict is unchanged — <c>false</c> — but its reason changed: an
    /// <c>allowed</c> here now means the root answered on its own grants instead of descending.
    /// </remarks>
    [Fact]
    public async Task TheRootsOwnAllowDoesNotAdmitWhatTheLeafRefuses()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Root, chain.RootId, Reader, queryRoles: true),
            "chain.reader passes the root's own queryRoles but not the leaf's stamped override; " +
            "an allowed verdict here means the root answered alone instead of the leaf deciding");
    }

    /// <summary>
    /// The control: the role the leaf's stamped override admits is allowed at every level that
    /// descends to it. Without it a blanket-deny bug would make the other tests green for the wrong
    /// reason.
    /// </summary>
    [Fact]
    public async Task TheLeafsAdmittedRoleIsAllowedAtEveryLevel()
    {
        var chain = await StartChainAsync();

        Assert.True(await IsAuthorizedAsync(Root, chain.RootId, Admin, queryRoles: true));
        Assert.True(await IsAuthorizedAsync(Mid, chain.MidId, Admin, queryRoles: true));
        Assert.True(await IsAuthorizedAsync(Leaf, chain.LeafId, Admin, queryRoles: true));
    }

    /// <summary>
    /// The read surface no longer refuses on <c>queryRoles</c> — that decision belongs to the
    /// gateway — whatever <c>authorize</c> says about the same caller.
    /// </summary>
    /// <remarks>
    /// Was <c>AuthorizeAgreesWithTheStateFunctionWhereBothResolveTheSameGrants</c>. Agreement stopped being
    /// the contract when the read gates were removed (2026-09-23); the name kept claiming it. The
    /// <c>authorize</c> call stays only so the helper's own guard (200/403 with a matching
    /// <c>allowed</c> body) runs for every role row.
    /// </remarks>
    [Theory]
    [InlineData(Reader)]
    [InlineData(Admin)]
    [InlineData(LeafAdmin)]
    [InlineData(MidAdmin)]
    [InlineData(null)]
    public async Task TheStateFunctionServesWhateverAuthorizeAnswers(string? roles)
    {
        var chain = await StartChainAsync();

        _ = await IsAuthorizedAsync(Leaf, chain.LeafId, roles, queryRoles: true);
        var (stateStatus, _) = await CallFunctionAsync(Leaf, chain.LeafId, "state", roles);

        var stateAllowed = stateStatus switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotModified => true,
            HttpStatusCode.Forbidden => false,
            _ => throw new Xunit.Sdk.XunitException(
                $"the state function answered {(int)stateStatus}, which is neither a read nor a refusal")
        };

        // The runtime no longer refuses here, so agreement is no longer the contract — the
        // separation is. `authorize` keeps its own verdict (asserted by
        // EnforcementPostureTests.AuthorizeKeepsRefusing) while the read surface serves; a state
        // function that still answered 403 would mean a gate survived the removal.
        Assert.True(stateAllowed,
            "the read surface must not refuse on queryRoles — that decision belongs to the gateway");
    }

    /// <summary>
    /// The leaf-only rule's defining case, "root denies + leaf allows": <c>chain.leaf-admin</c> is absent from the
    /// root's own allowlist and from the root's override of the mid, and is named by the mid's
    /// override of the leaf. While the instance is inside the SubFlow it is ALLOWED at the root and at
    /// the mid.
    /// </summary>
    /// <remarks>
    /// Was <c>ALeafOnlyRoleIsRefusedAtTheRoot</c>, which asserted <c>false</c> at the root (the
    /// conjunction refused at the root's own allowlist). Flipped to <c>true</c>, and extended to the
    /// mid, whose own grants (and the root's override of it) refuse the role as well.
    /// </remarks>
    [Fact]
    public async Task ARoleTheRootRefusesIsAllowedWhileTheLeafAdmitsIt()
    {
        var chain = await StartChainAsync();

        Assert.True(await IsAuthorizedAsync(Root, chain.RootId, LeafAdmin, queryRoles: true),
            "chain.leaf-admin is absent from the root's allowlist but admitted by the leaf; under the leaf-only rule " +
            "the leaf decides — a refusal here means the root's own grants were still ANDed in");
        Assert.True(await IsAuthorizedAsync(Mid, chain.MidId, LeafAdmin, queryRoles: true),
            "the mid's own grants and the root's override of the mid refuse chain.leaf-admin; the " +
            "mid is not the leaf, so neither may decide");
        Assert.True(await IsAuthorizedAsync(Leaf, chain.LeafId, LeafAdmin, queryRoles: true));
    }

    /// <summary>
    /// The leaf-only rule's other new case, "root allows only a narrow set + leaf declares nothing": the leaf's empty
    /// grant set allows, so a role the root refuses — and a caller with no roles at all — are
    /// allowed at the root while the instance is inside that SubFlow.
    /// </summary>
    /// <remarks>
    /// New. <c>root-open</c>'s own <c>queryRoles</c> admit <c>chain.admin</c> only; its child
    /// <c>leaf-open</c> declares no <c>queryRoles</c> on its state or workflow and the root stamps no
    /// override. Under the conjunction every assertion below except the admin control was
    /// <c>false</c>. A runtime that still let the root's grants decide would refuse
    /// <c>chain.reader</c> here.
    /// </remarks>
    [Fact]
    public async Task ALeafWithNoQueryRolesAllowsWhateverTheRootDeclares()
    {
        var (rootId, leafId) = await StartTwoLevelAsync(RootOpen, LeafOpen);

        Assert.True(await IsAuthorizedAsync(RootOpen, rootId, Admin, queryRoles: true),
            "control: the one role the root itself admits");
        Assert.True(await IsAuthorizedAsync(RootOpen, rootId, Reader, queryRoles: true),
            "the root's own allowlist refuses chain.reader, but the leaf declares nothing and an " +
            "empty set allows; a refusal means the root's grants still take part in the decision");
        Assert.True(await IsAuthorizedAsync(RootOpen, rootId, NoRole, queryRoles: true),
            "a role-less caller is allowed by an empty set");
        Assert.True(await IsAuthorizedAsync(LeafOpen, leafId, Reader, queryRoles: true),
            "addressed directly, the leaf answers the same as through its root");
    }

    /// <summary>A caller with no roles at all is refused by the leaf's allowlist at every level.</summary>
    [Fact]
    public async Task ARoleLessCallerIsRefused()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Root, chain.RootId, NoRole, queryRoles: true));
        Assert.False(await IsAuthorizedAsync(Mid, chain.MidId, NoRole, queryRoles: true));
        Assert.False(await IsAuthorizedAsync(Leaf, chain.LeafId, NoRole, queryRoles: true));
    }
}
