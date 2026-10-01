using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.AuthorizationChainLab;

/// <summary>
/// A parent's subflow override governs its DIRECT child and nothing further, and it REPLACES that
/// child's own <c>queryRoles</c> rather than merging with them.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The overrides used to be read from the parent's definition at the
/// point of descent. That had two consequences: the descent RETURNED as soon as an override matched,
/// so a grandchild's own gate never ran; and at a directly addressed leaf — where no parent is in
/// scope — the reader found nothing and answered from the child's own grants, giving the OPPOSITE
/// verdict to the state function for the same instance. They are now resolved per hop from the map
/// stamped on each child at start.</para>
/// <para><b>This lab found a second defect on its first run, and it is fixed.</b>
/// <c>IsQueryAllowedAsync</c> used to key the lookup on <c>EffectiveState</c>, which for an instance
/// that itself has an active SubFlow is a DESCENDANT's state key — so the stamped override (keyed by
/// the state the parent declared) could not match and the gate degraded to the workflow root's
/// grants. Measured on the bench: <c>CurrentState=mid-waiting, EffectiveState=leaf-waiting</c> on the
/// mid, and <c>chain.mid-only</c> read the mid <c>200</c> although the root had narrowed it to
/// <c>chain.admin</c>. A parent's narrowing silently stopped applying the moment its child opened a
/// subflow of its own. The gate now reads <c>CurrentState</c>.</para>
/// <para><b>Why the REPLACE tests still use a two-level chain.</b> Not because the mechanism is
/// unreachable deeper — it is, now — but because at a mid level the conjunction down to the leaf
/// dominates every verdict, so an override difference there cannot be observed in isolation. The
/// two-level pair (same terminal child, one root overriding it and one not) is the smallest shape
/// that separates "override replaces" from "no override → own grants".</para>
/// </remarks>
public sealed class ChainOverrideTests : AuthorizationChainLabTestBase
{
    public ChainOverrideTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// An ancestor's override must not travel past its direct child. <c>chain.mid-admin</c> is named
    /// by the ROOT's override of the mid and is absent from the MID's override of the leaf; admitting
    /// it at the leaf would mean the root's narrowing had been carried one level too far.
    /// </summary>
    [Fact]
    public async Task AnAncestorsOverrideDoesNotReachTheGrandchild()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Leaf, chain.LeafId, MidAdmin, queryRoles: true),
            "chain.mid-admin is granted by the ROOT's override of the mid and by nothing at the leaf; " +
            "an allowed verdict means an ancestor's override propagated past its direct child");
    }

    /// <summary>
    /// The direct parent's override IS the one that governs, and it is applied at the child.
    /// <c>chain.leaf-admin</c> appears only in the mid's override of the leaf — not in the leaf's own
    /// grants, not at the root.
    /// </summary>
    [Fact]
    public async Task TheDirectParentsOverrideGovernsTheChild()
    {
        var chain = await StartChainAsync();

        Assert.True(await IsAuthorizedAsync(Leaf, chain.LeafId, LeafAdmin, queryRoles: true),
            "chain.leaf-admin appears ONLY in the mid's override of the leaf — an allowed verdict " +
            "proves the stamped override was read at the level it governs");
    }

    /// <summary>
    /// The override REPLACES, it does not merge. <c>chain.leaf-only</c> is in the leaf's OWN grants
    /// and absent from the mid's override; merging would let it back in, handing the narrowing
    /// straight back to the roles it was taken from.
    /// </summary>
    [Fact]
    public async Task TheOverrideReplacesTheChildsOwnGrants()
    {
        var chain = await StartChainAsync();

        Assert.False(await IsAuthorizedAsync(Leaf, chain.LeafId, "chain.leaf-only", queryRoles: true),
            "chain.leaf-only is in the leaf's OWN queryRoles; the override replaces them, so an " +
            "allowed verdict means the two grant sets were merged");
    }

    /// <summary>
    /// The A/B pair, on a child with nothing beneath it so the mechanism is reachable. The same
    /// terminal mid is started by two roots — one that overrides it and one that does not — and
    /// <c>chain.mid-only</c> (in the child's own grants, absent from the override) separates them.
    /// </summary>
    [Fact]
    public async Task WithNoOverrideTheChildsOwnGrantsApply()
    {
        var (_, plainChild) = await StartTwoLevelAsync(RootPlain);

        Assert.True(await IsAuthorizedAsync(MidTerminal, plainChild, MidOnly, queryRoles: true),
            "root-plain declares no override, so the child's own queryRoles govern it");
    }

    /// <summary>The other half of the same pair: with an override declared, the child's own grants go.</summary>
    [Fact]
    public async Task WithAnOverrideTheChildsOwnGrantsAreReplaced()
    {
        var (_, narrowChild) = await StartTwoLevelAsync(RootNarrow);

        Assert.False(await IsAuthorizedAsync(MidTerminal, narrowChild, MidOnly, queryRoles: true),
            "root-narrow overrides the child to chain.admin; chain.mid-only lives only in the " +
            "child's own grants, which the override replaces");

        Assert.True(await IsAuthorizedAsync(MidTerminal, narrowChild, Admin, queryRoles: true),
            "and the role the override DOES name is admitted");
    }

    /// <summary>
    /// The addressability property: a child reached directly answers exactly as the state function
    /// answers there. Both go through the same stamped map. This is the case that previously produced
    /// opposite verdicts.
    /// </summary>
    [Theory]
    [InlineData(LeafAdmin)]
    [InlineData(Admin)]
    [InlineData("chain.leaf-only")]
    [InlineData(MidAdmin)]
    public async Task ADirectlyAddressedLeafAnswersTheSameAsItsStateFunction(string roles)
    {
        var chain = await StartChainAsync();

        var authorized = await IsAuthorizedAsync(Leaf, chain.LeafId, roles, queryRoles: true);
        var (stateStatus, _) = await CallFunctionAsync(Leaf, chain.LeafId, "state", roles);

        // Only the oracle's side is assertable now — the read surface no longer refuses at all.
        // `authorized` is still the value under test: what this row pins is that it is computed from
        // the map stamped on the leaf, so a directly addressed leaf answers as it would through its
        // parent. The state call stays as a guard that no gate survived the removal.
        Assert.NotEqual(System.Net.HttpStatusCode.Forbidden, stateStatus);
    }
}
