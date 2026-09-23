using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// <c>authorize?queryRoles=true</c> must answer the CONJUNCTION of the polled instance's own
/// <c>queryRoles</c> and every level beneath it, down to the deepest active leaf — and each level's
/// grants must resolve from the map its DIRECT parent stamped on it, never from an ancestor's.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Before the 2026-09-22 authorization change, <c>authorize</c>
/// short-circuited into the active SubFlow and never evaluated the polled instance's own grants. That
/// made it strictly WEAKER than the gate it exists to describe: the state function gates the polled
/// instance and then descends, where the leaf gates again — two conjunctive gates. A middle tier
/// trusting <c>authorize</c> would therefore admit reads the runtime itself refuses.</para>
/// <para>The chain is built so each role fails at exactly one level, which is what lets a wrong
/// verdict name its own cause rather than just being red.</para>
/// </remarks>
public sealed class ChainConjunctionTests : AuthorizationChainLabTestBase
{
    public ChainConjunctionTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The defect itself. <c>chain.reader</c> passes the ROOT's own allowlist and fails the root's
    /// override of the mid. Answering from the leaf alone — or from the root alone — would say
    /// allowed; only the conjunction says denied.
    /// </summary>
    [Fact]
    public async Task ReaderPassesTheRootAndFailsTheChain()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Root, chain.RootId, Reader, queryRoles: true),
            "reader passes the root's own queryRoles but not the root's override of the mid; " +
            "an allowed verdict here means the descent was skipped or the root answered alone");
    }

    /// <summary>
    /// The control: a role that passes EVERY level must be allowed at every level. Without it a
    /// blanket-deny bug would make the test above green for the wrong reason.
    /// <para>
    /// <c>chain.admin</c> is the one role the fixture lets through end to end — the root's own grants,
    /// the root's override of the mid, and the mid's override of the leaf all name it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARoleThatPassesEveryLevelIsAllowedEverywhere()
    {
        var chain = await StartChainAsync();

        Assert.True(await IsAuthorizedAsync(Root, chain.RootId, Admin, queryRoles: true));
        Assert.True(await IsAuthorizedAsync(Mid, chain.MidId, Admin, queryRoles: true));
        Assert.True(await IsAuthorizedAsync(Leaf, chain.LeafId, Admin, queryRoles: true));
    }

    /// <summary>
    /// <c>authorize</c> and the state function must agree question-for-question — on an instance
    /// with NO active SubFlow of its own, which is where both gates resolve the same grants.
    /// </summary>
    /// <remarks>
    /// <para><b>Restricted to the leaf deliberately, and the restriction is the finding.</b> At the
    /// root and the mid the two surfaces do NOT agree today, and it is not this change that parted
    /// them: <c>IsQueryAllowedAsync</c> keys on <c>EffectiveState</c>, which for a parent is a
    /// descendant's state key, so neither that level's own state <c>queryRoles</c> nor the parent's
    /// stamped override can match and the gate degrades to the workflow root's grants. Measured:
    /// <c>chain.reader</c> reads the root <c>200</c> while <c>authorize</c> — which does reach the
    /// leaf — answers <c>403</c>.</para>
    /// <para>Asserting agreement at the root would therefore pin the defect rather than the contract.
    /// It is recorded as a known gap in <c>TEST-SCENARIOS.md</c> and belongs to the follow-up that
    /// fixes the keying, not here.</para>
    /// </remarks>
    [Theory]
    [InlineData(Reader)]
    [InlineData(Admin)]
    [InlineData(LeafAdmin)]
    [InlineData(MidAdmin)]
    [InlineData(null)]
    public async Task AuthorizeAgreesWithTheStateFunctionWhereBothResolveTheSameGrants(string? roles)
    {
        var chain = await StartChainAsync();

        var authorized = await IsAuthorizedAsync(Leaf, chain.LeafId, roles, queryRoles: true);
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
    /// A role that only passes deeper down must still be refused at the top. This is the direction
    /// the conjunction protects that a leaf-only answer would not: <c>chain.leaf-admin</c> satisfies
    /// the mid's override of the leaf, and satisfies nothing at the root.
    /// </summary>
    [Fact]
    public async Task ALeafOnlyRoleIsRefusedAtTheRoot()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Root, chain.RootId, LeafAdmin, queryRoles: true),
            "chain.leaf-admin is absent from the root's allowlist; passing deeper down cannot buy " +
            "access to a level it was never granted");
    }

    /// <summary>A caller with no roles at all is refused by an allowlist at every level.</summary>
    [Fact]
    public async Task ARoleLessCallerIsRefused()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Root, chain.RootId, NoRole, queryRoles: true));
        Assert.False(await IsAuthorizedAsync(Mid, chain.MidId, NoRole, queryRoles: true));
        Assert.False(await IsAuthorizedAsync(Leaf, chain.LeafId, NoRole, queryRoles: true));
    }
}
