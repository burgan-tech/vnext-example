using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// The <c>authorize</c> function — the surface the middle tier asks before it offers an action.
/// <para>
/// It answers three different questions depending on which target it is given, and exactly one
/// target may be named per call:
/// <list type="bullet">
///   <item><c>transitionKey</c> — may this caller run this transition here?</item>
///   <item><c>functionKey</c> — may this caller invoke this function?</item>
///   <item><c>queryRoles=true</c> — may this caller read this instance at all?</item>
/// </list>
/// </para>
/// <para>
/// The property that matters most is AGREEMENT: for a transition, <c>authorize</c> must answer
/// exactly what <c>availableTransitions</c> shows. The two surfaces have diverged before — once
/// because <c>authorize</c> ignored the instance's current state, and once because a surface built
/// its evaluation without the request context, leaving dynamic grants unable to match on one side
/// and able on the other. Every transition case below is asserted against the listing, not against
/// a hard-coded expectation.
/// </para>
/// </summary>
public class AuthorizeFunctionTests : RoleMatrixLabTestBase
{
    public AuthorizeFunctionTests(VNextTestEnvironment environment) : base(environment) { }

    // ── transition target ────────────────────────────────────────────────────

    /// <summary>
    /// The agreement test, run over every transition in <c>review</c> and every role. This is the
    /// single most valuable assertion in the suite: it does not encode what the answer should be,
    /// only that the two surfaces cannot disagree about it.
    /// </summary>
    [Fact]
    public async Task Authorize_AgreesWithTheStateFunctionListing_ForEveryTransitionAndRole()
    {
        var instanceId = await StartCaseInReviewAsync("agreement");

        string[] transitions = ["approve", "reject", "escalate", "open-review-note", "record-note"];

        // Only roles that can READ the state in `review`; the listing is unavailable to the others.
        foreach (var role in new[] { Approver, Auditor })
        {
            var offered = await AvailableTransitionKeysAsync(instanceId, role);

            foreach (var transition in transitions)
            {
                var authorized = await IsAuthorizedAsync(instanceId, role, transitionKey: transition);
                var listed = offered.Contains(transition);

                Assert.True(authorized == listed,
                    $"'{transition}' for {role}: authorize said {(authorized ? "allowed" : "denied")} " +
                    $"but the state function {(listed ? "offered it" : "did not offer it")}");
            }
        }
    }

    [Fact]
    public async Task Authorize_AllowsTheApproverToApprove()
    {
        var instanceId = await StartCaseInReviewAsync("authorize-approve");

        Assert.True(await IsAuthorizedAsync(instanceId, Approver, transitionKey: "approve"));
        Assert.False(await IsAuthorizedAsync(instanceId, Auditor, transitionKey: "approve"));
    }

    /// <summary>
    /// <c>authorize</c> is state-aware. <c>escalate</c> lives in <c>review</c>; asking about it
    /// while the instance sits in <c>intake</c> must be denied, or the middle tier would offer an
    /// action the execution policy then refuses.
    /// </summary>
    [Fact]
    public async Task Authorize_DeniesATransitionThatIsNotAvailableInTheCurrentState()
    {
        var instanceId = await StartCaseAsync("authorize-state-aware");

        Assert.False(await IsAuthorizedAsync(instanceId, Approver, transitionKey: "escalate"));

        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);

        Assert.True(await IsAuthorizedAsync(instanceId, Approver, transitionKey: "escalate"));
    }

    /// <summary>
    /// The <c>availableIn</c> AND narrowing must be visible here too, not only in the listing.
    /// <c>exit-role-matrix</c> is granted to the auditor and narrowed to <c>review</c>.
    /// </summary>
    [Fact]
    public async Task Authorize_AppliesTheAvailableInNarrowing()
    {
        var instanceId = await StartCaseAsync("authorize-narrowing");

        Assert.False(await IsAuthorizedAsync(instanceId, Auditor, transitionKey: "exit-role-matrix"));

        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);

        Assert.True(await IsAuthorizedAsync(instanceId, Auditor, transitionKey: "exit-role-matrix"));
        Assert.False(await IsAuthorizedAsync(instanceId, Approver, transitionKey: "exit-role-matrix"));
    }

    [Fact]
    public async Task Authorize_DeniesAnUnknownTransition()
    {
        var instanceId = await StartCaseInReviewAsync("authorize-unknown");

        Assert.False(await IsAuthorizedAsync(instanceId, Approver, transitionKey: "no-such-transition"));
    }

    // ── function target ──────────────────────────────────────────────────────

    /// <summary>
    /// A function's declared <c>roles</c> are evaluated HERE and nowhere else. The approver is
    /// granted, the viewer is denied, and the maker matches no ALLOW in the allowlist.
    /// </summary>
    [Fact]
    public async Task Authorize_EvaluatesTheFunctionsDeclaredRoles()
    {
        var instanceId = await StartCaseAsync("authorize-function");

        Assert.True(await IsAuthorizedAsync(instanceId, Approver, functionKey: "role-matrix-summary"));
        Assert.True(await IsAuthorizedAsync(instanceId, Auditor, functionKey: "role-matrix-summary"));
        Assert.False(await IsAuthorizedAsync(instanceId, Maker, functionKey: "role-matrix-summary"));
        Assert.False(await IsAuthorizedAsync(instanceId, Viewer, functionKey: "role-matrix-summary"));
    }

    // ── queryRoles target ────────────────────────────────────────────────────

    /// <summary>
    /// <c>queryRoles=true</c> must answer the same thing the read functions do. A middle tier that
    /// asks first and then reads should never see the two disagree.
    /// </summary>
    [Fact]
    public async Task Authorize_QueryRoles_MatchesWhatTheReadFunctionsDo()
    {
        var instanceId = await StartCaseAsync("authorize-query-intake");

        foreach (var role in new[] { Maker, Approver, Auditor, Viewer })
        {
            var authorized = await IsAuthorizedAsync(instanceId, role, queryRoles: true);
            var (readStatus, _) = await CallInstanceFunctionAsync(instanceId, "state", role);

            Assert.True(authorized == (readStatus == HttpStatusCode.OK),
                $"{role}: authorize?queryRoles said {(authorized ? "allowed" : "denied")} " +
                $"but the state function answered {(int)readStatus}");
        }
    }

    /// <summary>
    /// Same agreement check once the instance sits in a state that overrides the root set — the case
    /// where the two surfaces would diverge if one of them resolved the state and the other did not.
    /// </summary>
    [Fact]
    public async Task Authorize_QueryRoles_FollowsTheStateOverride()
    {
        var instanceId = await StartCaseInReviewAsync("authorize-query-review");

        Assert.False(await IsAuthorizedAsync(instanceId, Maker, queryRoles: true));
        Assert.True(await IsAuthorizedAsync(instanceId, Approver, queryRoles: true));
        Assert.True(await IsAuthorizedAsync(instanceId, Auditor, queryRoles: true));
    }

    // ── request shape ────────────────────────────────────────────────────────

    /// <summary>
    /// Exactly one target, never zero and never two. A call naming none is a client bug that would
    /// otherwise get a confident-looking answer about nothing in particular.
    /// </summary>
    [Fact]
    public async Task Authorize_RejectsACallThatNamesNoTarget()
    {
        var instanceId = await StartCaseAsync("authorize-no-target");

        var (status, _) = await AuthorizeAsync(instanceId, Approver);

        Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity,
            $"a target-less authorize answered {(int)status}; it should be rejected as a bad request");
    }

    [Fact]
    public async Task Authorize_RejectsACallThatNamesTwoTargets()
    {
        var instanceId = await StartCaseAsync("authorize-two-targets");

        var (status, _) = await AuthorizeAsync(
            instanceId, Approver, transitionKey: "approve", functionKey: "role-matrix-summary");

        Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity,
            $"an authorize naming two targets answered {(int)status}; it should be rejected");
    }

    /// <summary>
    /// A caller with no roles is answered, not errored: <c>authorize</c> reports "denied" rather
    /// than failing. The distinction matters to a middle tier that treats a non-403 error as
    /// retryable.
    /// </summary>
    [Fact]
    public async Task Authorize_DeniesARoleLessCaller_WithoutErroring()
    {
        var instanceId = await StartCaseAsync("authorize-no-role");

        var (status, _) = await AuthorizeAsync(instanceId, NoRole, transitionKey: "submit-for-review");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }
}
