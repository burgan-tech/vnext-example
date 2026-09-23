using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowOverrideLab;

/// <summary>
/// <c>overrides.states.&lt;childState&gt;.views</c> and <c>overrides.transitions.&lt;childTransition&gt;.views</c>:
/// the parent swaps the reference of the view the CHILD's own rules selected, scoped to one state or
/// one transition.
/// </summary>
/// <remarks>
/// The legacy flat <c>overrides.views</c> map swapped a view key everywhere at once and was applied
/// parent-side after descent. The scoped form is resolved child-side on the child's own
/// <c>CurrentState</c>, so it also applies to a directly addressed child, and a state override never
/// falls through to a transition view (or the reverse).
/// </remarks>
public sealed class ViewOverrideTests : SubflowOverrideLabTestBase
{
    public ViewOverrideTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task AStateScopedOverrideAppliesViaTheParentAndOnTheDirectlyAddressedChild()
    {
        var (parentId, childId) = await StartPairAsync(Parent, Child, ChildAck);
        await WaitForLeafStateAsync(Parent, parentId, "lp-wait", ChildAck);

        Assert.Equal(ParentLpView, await ViewKeyAsync(Parent, parentId, ChildAck));
        Assert.Equal(ParentLpView, await ViewKeyAsync(Child, childId, ChildAck));
    }

    [Fact]
    public async Task ATransitionScopedOverrideAppliesToThatTransitionsView()
    {
        var (parentId, childId) = await StartPairAsync(Parent, Child, ChildAck);
        await WaitForLeafStateAsync(Parent, parentId, "lp-wait", ChildAck);

        Assert.Equal(ParentConfirmView, await ViewKeyAsync(Parent, parentId, ChildAck, "confirm"));
        Assert.Equal(ParentConfirmView, await ViewKeyAsync(Child, childId, ChildAck, "confirm"));
    }

    /// <summary>
    /// <c>note</c>'s transition view has the SAME key the state override swaps, and no transition
    /// override. A transition view is looked up under <c>transitions</c> only, so it must stay the child's.
    /// </summary>
    [Fact]
    public async Task AStateOverrideDoesNotLeakIntoATransitionView()
    {
        var (parentId, childId) = await StartPairAsync(Parent, Child, ChildAck);
        await WaitForLeafStateAsync(Parent, parentId, "lp-wait", ChildAck);

        Assert.Equal(ChildLpView, await ViewKeyAsync(Parent, parentId, ChildAck, "note"));
        Assert.Equal(ChildLpView, await ViewKeyAsync(Child, childId, ChildAck, "note"));
    }

    /// <summary>
    /// A replacement that cannot be resolved falls back to the child's own view (EventId 20101 is
    /// checked out-of-band) — never an error to the client.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableOverrideFallsBackToTheChildsView()
    {
        var (parentId, childId) = await StartPairAsync(ParentRoles, Child, ParentAck);
        await WaitForLeafStateAsync(ParentRoles, parentId, "lp-wait", ParentAck);

        Assert.Equal(ChildLpView, await ViewKeyAsync(ParentRoles, parentId, ParentAck));
        Assert.Equal(ChildLpView, await ViewKeyAsync(Child, childId, ParentAck));
    }

    /// <summary>One hop: TOP's view override names a grandchild state and must not reach it.</summary>
    [Fact]
    public async Task AnAncestorsViewOverrideDoesNotReachTheGrandchild()
    {
        var (topId, midId) = await StartPairAsync(Top, Mid, ChildAck);
        string? leafId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(Mid, midId, ChildAck);
            return subs.TryGetValue(Child, out leafId);
        }, $"{Mid} {midId} should open a correlation to {Child}");
        await WaitForLeafStateAsync(Top, topId, "lp-wait", ChildAck);

        Assert.Equal(ChildLpView, await ViewKeyAsync(Top, topId, ChildAck));
        Assert.Equal(ChildLpView, await ViewKeyAsync(Child, leafId!, ChildAck));
    }

    /// <summary>
    /// Regression: the deprecated parent-side map keeps today's behaviour — applied when the PARENT is
    /// polled (after descent), not when the child is addressed directly.
    /// </summary>
    [Fact]
    public async Task TheLegacyViewMapStillAppliesParentSideOnly()
    {
        var (parentId, childId) = await StartPairAsync(ParentLegacy, Child, ChildAck);
        await WaitForLeafStateAsync(ParentLegacy, parentId, "lp-wait", ChildAck);

        Assert.Equal(LegacyView, await ViewKeyAsync(ParentLegacy, parentId, ChildAck));
        Assert.Equal(ChildLpView, await ViewKeyAsync(Child, childId, ChildAck));
    }
}
