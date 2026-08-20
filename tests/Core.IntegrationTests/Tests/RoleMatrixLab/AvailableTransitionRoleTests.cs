using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// <c>transition.roles</c> and <c>availableIn</c> — which transitions the state function OFFERS.
/// <para>
/// This is a discovery surface, not a capability boundary: the runtime does not enforce
/// <c>transition.roles</c> at <c>POST .../transitions/{key}</c>, by design. These tests therefore
/// assert on what appears in <c>availableTransitions</c>, never on whether a transition can be run.
/// <c>AuthorizeFunctionTests</c> covers the other half — that <c>authorize</c> answers the same way
/// the listing does.
/// </para>
/// <para>
/// The <c>review</c> state holds four transitions with deliberately different grant shapes, so one
/// listing call exercises every rule the evaluator has:
/// <list type="bullet">
///   <item><c>approve</c> — allowlist (one ALLOW, everyone else out)</item>
///   <item><c>reject</c> — blacklist (deny-only set: everyone except the named role)</item>
///   <item><c>escalate</c> — predefined <c>$InstanceStarter</c> (caller identity, not role strings)</item>
///   <item><c>open-review-note</c> — no grants at all (always offered)</item>
/// </list>
/// </para>
/// </summary>
public class AvailableTransitionRoleTests : RoleMatrixLabTestBase
{
    public AvailableTransitionRoleTests(VNextTestEnvironment environment) : base(environment) { }

    // ── allowlist ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_IsOfferedOnlyToTheApprover()
    {
        var instanceId = await StartCaseInReviewAsync("allowlist");

        Assert.Contains("approve", await AvailableTransitionKeysAsync(instanceId, Approver));
        Assert.DoesNotContain("approve", await AvailableTransitionKeysAsync(instanceId, Auditor));
    }

    // ── blacklist ────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>reject</c> declares a deny-only set. A set with no ALLOW grant is a blacklist: everyone
    /// is offered it EXCEPT the explicitly denied role. Getting this backwards — treating a
    /// deny-only set as an allowlist that nobody matches — would make the transition vanish for
    /// every caller, which is a silent and easily missed regression.
    /// </summary>
    [Fact]
    public async Task Reject_IsOfferedToEveryoneExceptTheDeniedRole()
    {
        var instanceId = await StartCaseInReviewAsync("blacklist");

        Assert.Contains("reject", await AvailableTransitionKeysAsync(instanceId, Approver));
        Assert.DoesNotContain("reject", await AvailableTransitionKeysAsync(instanceId, Auditor));
    }

    // ── predefined ───────────────────────────────────────────────────────────

    /// <summary>
    /// <c>escalate</c> is granted to <c>$InstanceStarter</c>, which matches the caller's actor
    /// identity rather than any role string. The approver who started the case is offered it.
    /// <para>
    /// This is the grant most exposed to a change of caller-role provider: the role SET may now come
    /// from an external IDM, but the identity a predefined grant matches on does not. If this test
    /// starts failing after a provider switch, the identity plumbing broke, not the role plumbing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Escalate_IsOfferedToTheInstanceStarter()
    {
        var instanceId = await StartCaseInReviewAsync("predefined", startRoles: Approver);

        Assert.Contains("escalate", await AvailableTransitionKeysAsync(instanceId, Approver));
    }

    // ── no grants ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGrantLessTransition_IsOfferedToEveryCallerThatPassesTheQueryGate()
    {
        var instanceId = await StartCaseInReviewAsync("no-grants");

        foreach (var role in new[] { Approver, Auditor })
        {
            Assert.Contains("open-review-note", await AvailableTransitionKeysAsync(instanceId, role));
        }
    }

    // ── availableIn AND narrowing ────────────────────────────────────────────

    /// <summary>
    /// The <c>record-note</c> shared transition is the AND-narrowing case, and the reason this
    /// fixture exists in this shape.
    /// <para>
    /// Its own grants allow BOTH maker and approver. Its <c>availableIn</c> lists <c>intake</c> as a
    /// bare state key — no narrowing — and <c>review</c> as an object that allows only the approver.
    /// The two levels compose as AND, so in <c>intake</c> the maker sees it and in <c>review</c> it
    /// disappears for the maker while staying for the approver. A merge implemented as OR, or one
    /// that ignored the per-state grants, would leave it visible in both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RecordNote_IsOfferedToTheMakerInIntake()
    {
        var instanceId = await StartCaseAsync("and-narrowing-intake");

        Assert.Contains("record-note", await AvailableTransitionKeysAsync(instanceId, Maker));
        Assert.Contains("record-note", await AvailableTransitionKeysAsync(instanceId, Approver));
    }

    /// <summary>
    /// In <c>review</c> the per-state entry narrows to the approver only. The maker cannot be used
    /// to read this listing at all (the state's queryRoles deny it), so the narrowing is asserted
    /// through a caller holding BOTH roles: the transition-level gate allows it via either role, and
    /// only the per-state entry can remove <c>record-note</c> — which it must not, since that caller
    /// also holds approver. The negative half is the maker-only caller's 403 in
    /// <see cref="QueryRoleGateTests"/>.
    /// </summary>
    [Fact]
    public async Task RecordNote_SurvivesTheReviewNarrowing_ForTheApprover()
    {
        var instanceId = await StartCaseInReviewAsync("and-narrowing-review");

        Assert.Contains("record-note", await AvailableTransitionKeysAsync(instanceId, Approver));
        Assert.Contains("record-note", await AvailableTransitionKeysAsync(instanceId, $"{Maker},{Approver}"));
    }

    /// <summary>
    /// The auditor holds neither of the transition-level grants, so the transition-level gate alone
    /// already removes <c>record-note</c> — before <c>availableIn</c> is even consulted. Pinned so
    /// that a narrowing bug cannot be mistaken for correct behaviour here.
    /// </summary>
    [Fact]
    public async Task RecordNote_IsNotOfferedToARoleTheTransitionNeverGrants()
    {
        var instanceId = await StartCaseInReviewAsync("and-narrowing-auditor");

        Assert.DoesNotContain("record-note", await AvailableTransitionKeysAsync(instanceId, Auditor));
    }

    // ── well-known transitions ───────────────────────────────────────────────

    /// <summary>
    /// <c>cancel</c>, <c>updateData</c> and <c>exit</c> are listed like any other transition and are
    /// role-filtered like any other transition. They are listed under their CONFIGURED key, never
    /// under the reserved alias, and carry a <c>kind</c> discriminator.
    /// </summary>
    [Fact]
    public async Task Cancel_IsListedUnderItsConfiguredKey_WithItsKind()
    {
        var instanceId = await StartCaseAsync("well-known-cancel");

        var keys = await AvailableTransitionKeysAsync(instanceId, Approver);

        Assert.Contains("cancel-role-matrix", keys);
        Assert.DoesNotContain("cancel", keys);
        Assert.Equal("cancel", await TransitionKindAsync(instanceId, Approver, "cancel-role-matrix"));
    }

    [Fact]
    public async Task UpdateData_IsRoleFilteredLikeAnyOtherTransition()
    {
        var instanceId = await StartCaseAsync("well-known-update");

        Assert.Contains("update-role-matrix-data", await AvailableTransitionKeysAsync(instanceId, Maker));
        Assert.DoesNotContain("update-role-matrix-data", await AvailableTransitionKeysAsync(instanceId, Auditor));
        Assert.Equal("updateData", await TransitionKindAsync(instanceId, Maker, "update-role-matrix-data"));
    }

    /// <summary>
    /// <c>exit</c> carries BOTH a transition-level grant and an <c>availableIn</c> entry that names
    /// only <c>review</c>. In <c>intake</c> the state gate alone removes it, even for the auditor
    /// the transition grants — which is the <c>availableIn</c> state filter working independently of
    /// its role narrowing.
    /// </summary>
    [Fact]
    public async Task Exit_IsOfferedOnlyInTheStateItsAvailableInNames()
    {
        var instanceId = await StartCaseAsync("well-known-exit");

        Assert.DoesNotContain("exit-role-matrix", await AvailableTransitionKeysAsync(instanceId, Auditor));

        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);

        Assert.Contains("exit-role-matrix", await AvailableTransitionKeysAsync(instanceId, Auditor));
        Assert.DoesNotContain("exit-role-matrix", await AvailableTransitionKeysAsync(instanceId, Approver));
    }
}
