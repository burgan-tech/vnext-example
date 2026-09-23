using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowOverrideLab;

/// <summary>
/// <c>overrides.states.&lt;childState&gt;.interaction.longPoll</c>: the parent tunes the window and the
/// roles of a long-poll its SubFlow child declares.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The fallback window is consumed inside the CHILD's pipeline
/// (<c>HandleLongPollTerminationStep</c>, order 75), where the parent's definition is not in scope, so
/// until this change a parent had no way to set it. The override is stamped onto the child at start
/// and resolved by <c>Instance.ResolveEffectiveLongPoll</c> — one resolver behind the arm, the gate
/// and the state body. These tests pin that the three agree, via the parent AND with the child
/// addressed directly.</para>
/// <para>The child declares <c>terminate: true, fallbackTimeoutSeconds: 600, roles: [ovr.child-ack]</c>.</para>
/// </remarks>
public sealed class LongPollOverrideTests : SubflowOverrideLabTestBase
{
    public LongPollOverrideTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Duration-only override: the body reports the PARENT's window, and the child's <c>terminate</c>
    /// and <c>roles</c> are kept because the override leaves them out (field-level replace).
    /// </summary>
    [Fact]
    public async Task ADurationOverrideReplacesTheWindowAndKeepsTheChildsRolesAndTerminate()
    {
        var (parentId, childId) = await StartPairAsync(Parent, Child, ChildAck);
        await WaitForLeafStateAsync(Parent, parentId, "lp-wait", ChildAck);

        foreach (var (workflow, id) in new[] { (Parent, parentId), (Child, childId) })
        {
            var interaction = await InteractionAsync(workflow, id, ChildAck);
            Assert.True(interaction.HasValue,
                $"{workflow}: the child is parked on its long-poll and ovr.child-ack is the child's own role, " +
                "which a duration-only override must keep — the interaction block must be served");

            Assert.Equal(120, interaction!.Value.GetProperty("fallbackTimeoutSeconds").GetInt32());
            Assert.True(interaction.Value.GetProperty("terminateLongPoll").GetBoolean(),
                "terminate is never overridable and the child declares true");

            Assert.True(await AckAllowedAsync(workflow, id, ChildAck),
                $"{workflow}: the child's own role must still be admitted to acknowledge");

            // The override did not name roles, so it must not have widened them either.
            Assert.Null(await InteractionAsync(workflow, id, ParentAck));
            Assert.False(await AckAllowedAsync(workflow, id, ParentAck),
                $"{workflow}: ovr.parent-ack is granted by no one here");
        }
    }

    /// <summary>
    /// The window the body reports must be the window the job was ARMED with. Since runtime
    /// <c>5f60ab4b</c> the long-poll ack <c>InstanceJobs</c> row carries <c>ExecuteAt</c>, but no read
    /// surface exposes it and this suite has no database access, so it is measured by behaviour — which
    /// is also the stronger claim (the job actually fires, not just was scheduled): a 5 s override on a
    /// 600 s child must resume the child — token cleared, interaction gone, status Active — within seconds.
    /// </summary>
    [Fact]
    public async Task TheArmedFallbackJobFiresOnTheParentsWindow()
    {
        var (parentId, childId) = await StartPairAsync(ParentShort, Child, ChildAck);

        var interaction = await InteractionAsync(Child, childId, ChildAck);
        // Tolerate the rare case the 5 s job already fired before the first read.
        if (interaction.HasValue)
            Assert.Equal(5, interaction.Value.GetProperty("fallbackTimeoutSeconds").GetInt32());

        await WaitUntilAsync(async () =>
        {
            var body = await StateBodyAsync(Child, childId, ChildAck);
            return body.GetProperty("status").GetString() == "A"
                   && !body.TryGetProperty("interaction", out _);
        }, $"{Child} {childId} should be resumed by the fallback job armed with the parent's 5 s window " +
           $"(the child's own window is {ChildWindowSeconds} s)", TimeSpan.FromSeconds(45));

        var (state, _) = await GetInstanceStateAsync(Child, childId, ChildAck);
        Assert.Equal("lp-wait", state);
        await AssertNotFaultedAsync(ParentShort, parentId, ChildAck);
    }

    /// <summary>
    /// Roles-only override: the parent's list REPLACES the child's. A role granted only by the parent
    /// receives the interaction and may acknowledge; the child's own role is refused — via the parent
    /// and with the child addressed directly. The window stays the child's.
    /// </summary>
    [Fact]
    public async Task ARolesOverrideReplacesTheChildsGrantsOnEverySurface()
    {
        var (parentId, childId) = await StartPairAsync(ParentRoles, Child, ParentAck);
        await WaitForLeafStateAsync(ParentRoles, parentId, "lp-wait", ParentAck);

        foreach (var (workflow, id) in new[] { (ParentRoles, parentId), (Child, childId) })
        {
            var granted = await InteractionAsync(workflow, id, ParentAck);
            Assert.True(granted.HasValue,
                $"{workflow}: ovr.parent-ack is granted only by the parent's override and must receive the interaction");
            Assert.Equal(ChildWindowSeconds, granted!.Value.GetProperty("fallbackTimeoutSeconds").GetInt32());
            Assert.True(await AckAllowedAsync(workflow, id, ParentAck));

            Assert.Null(await InteractionAsync(workflow, id, ChildAck));
            Assert.False(await AckAllowedAsync(workflow, id, ChildAck),
                $"{workflow}: ovr.child-ack is the child's own grant, which the override REPLACED — not merged");
        }
    }

    /// <summary>
    /// The arm reads the same resolver: a starter holding only the child's own role is not an owner of
    /// the stop once the parent replaced the roles, so the child must NOT pause.
    /// </summary>
    [Fact]
    public async Task TheArmHonoursTheRolesOverride()
    {
        var (parentId, childId) = await StartPairAsync(ParentRoles, Child, ChildAck);
        await WaitForLeafStateAsync(ParentRoles, parentId, "lp-wait", ChildAck);

        var body = await StateBodyAsync(Child, childId, ParentAck);
        Assert.Equal("A", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("interaction", out _),
            "the triggering caller held ovr.child-ack only; under the parent's roles override that is not " +
            "an owner of the stop, so the arm must not have paused the child");
    }

    /// <summary>
    /// An override never ADDS a long-poll: the plain child's state declares none, so there is no
    /// interaction block and the instance rests Active (log EventId 20305 is checked out-of-band).
    /// </summary>
    [Fact]
    public async Task AnOverrideOnAStateWithoutALongPollIsIgnored()
    {
        var (parentId, childId) = await StartPairAsync(ParentNoLongPoll, PlainChild, ChildAck);
        await WaitForLeafStateAsync(ParentNoLongPoll, parentId, "plain-wait", ChildAck);

        foreach (var (workflow, id) in new[] { (ParentNoLongPoll, parentId), (PlainChild, childId) })
        {
            var body = await StateBodyAsync(workflow, id, ChildAck);
            Assert.False(body.TryGetProperty("interaction", out _),
                $"{workflow}: plain-wait declares no long-poll; the parent's override must not create one");
            Assert.True(await AckAllowedAsync(workflow, id, ChildAck),
                "nothing is awaiting, and authorize?ack=true answers allowed there");
        }

        var (_, status) = await GetInstanceStateAsync(PlainChild, childId, ChildAck);
        Assert.Equal("A", status);
    }

    /// <summary>
    /// One hop: TOP's override names <c>lp-wait</c>, a state that exists only in the GRANDCHILD. It must
    /// not reach it — the grandchild keeps its own 600 s window and its own roles.
    /// </summary>
    [Fact]
    public async Task AnAncestorsOverrideDoesNotReachTheGrandchild()
    {
        var (topId, midId) = await StartPairAsync(Top, Mid, ChildAck);

        string? leafId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(Mid, midId, ChildAck);
            return subs.TryGetValue(Child, out leafId);
        }, $"{Mid} {midId} should open a correlation to {Child}");
        await WaitForLeafStateAsync(Top, topId, "lp-wait", ChildAck);

        foreach (var (workflow, id) in new[] { (Top, topId), (Child, leafId!) })
        {
            var interaction = await InteractionAsync(workflow, id, ChildAck);
            Assert.True(interaction.HasValue,
                $"{workflow}: the grandchild's own role must still own and receive the interaction — " +
                "had TOP's roles override leaked, ovr.child-ack would not even have armed the pause");
            Assert.Equal(ChildWindowSeconds, interaction!.Value.GetProperty("fallbackTimeoutSeconds").GetInt32());

            Assert.Null(await InteractionAsync(workflow, id, ParentAck));
            Assert.False(await AckAllowedAsync(workflow, id, ParentAck));
        }
    }
}
