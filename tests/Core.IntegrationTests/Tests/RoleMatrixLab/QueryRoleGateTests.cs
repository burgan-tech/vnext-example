using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// <c>queryRoles</c> — the read-visibility rule of an instance, ANSWERED by
/// <c>authorize?queryRoles=true</c> and NOT enforced by the read functions.
/// <para>
/// The runtime used to gate every built-in read (<c>state</c>, <c>data</c>, <c>schema</c>,
/// <c>master</c>, <c>view</c>) with a 403. That gate was removed (council
/// <c>2026-09-22-remove-execution-authorization</c>, gates deleted 2026-09-23): the decision belongs
/// to the gateway, which asks <c>authorize?queryRoles=true</c> before forwarding a read. This class
/// was written against the gate and stayed red (16 tests) until 2026-10-05; it now pins both halves
/// of the current contract:
/// </para>
/// <list type="number">
/// <item>The RULE is unchanged and is what <c>authorize</c> answers: the state's own
/// <c>queryRoles</c> REPLACE the root's (<c>review</c> denies the maker the root allows), a role
/// outside an allowlist is refused, a role-less caller is refused, and a DENY is not bought back by
/// another role the caller also holds (Kleene evaluation, vnext #1057).</item>
/// <item>The read functions answer every caller — no 403 anywhere. If someone reinstates the gate,
/// <see cref="ReadFunctions_DoNotEnforceQueryRoles"/> fails.</item>
/// </list>
/// <para>
/// Which read functions exist depends on the state: the fixture has a view only in <c>review</c>,
/// and <c>schema</c> needs a <c>transitionKey</c> whose transition carries a schema
/// (<c>approve</c>, in <c>review</c>). Calling them elsewhere answers 404, which says nothing about
/// authorization — that is why the old theory's <c>view</c> / <c>schema</c> rows were red even
/// before the gate was removed.
/// </para>
/// </summary>
public class QueryRoleGateTests : RoleMatrixLabTestBase
{
    public QueryRoleGateTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>Built-in read functions that resolve in <c>intake</c>.</summary>
    private static readonly string[] IntakeReads = ["state", "data", "master"];

    /// <summary>Built-in read functions that resolve in <c>review</c>.</summary>
    private static readonly string[] ReviewReads = ["state", "data", "master", "view", "schema?transitionKey=approve"];

    private Task<bool> MayReadAsync(string instanceId, string? roles) =>
        IsAuthorizedAsync(instanceId, roles, queryRoles: true);

    // ── root queryRoles (intake declares none, so the root set applies) ─────

    [Fact]
    public async Task Intake_AllowsEveryRoleTheRootGrants()
    {
        var instanceId = await StartCaseAsync("root-allow");

        foreach (var role in new[] { Maker, Approver, Auditor })
            Assert.True(await MayReadAsync(instanceId, role), $"{role} is granted by the root queryRoles");
    }

    /// <summary>
    /// The root set is an allowlist: it names three roles and grants nothing else. A caller holding
    /// a role that appears nowhere in the set is refused.
    /// </summary>
    [Fact]
    public async Task Intake_RefusesARoleTheRootNeverGrants()
    {
        var instanceId = await StartCaseAsync("root-deny");

        Assert.False(await MayReadAsync(instanceId, Viewer));
    }

    /// <summary>
    /// A caller with no roles at all is still evaluated — it just matches no ALLOW in an allowlist.
    /// Pinned separately from the viewer: a role-less caller takes a different path through the
    /// evaluator, and a regression that skips evaluation entirely would let it through.
    /// </summary>
    [Fact]
    public async Task ARoleLessCaller_IsRefused()
    {
        var instanceId = await StartCaseAsync("no-role");

        Assert.False(await MayReadAsync(instanceId, NoRole));
    }

    // ── state queryRoles replace the root's ──────────────────────────────────

    /// <summary>
    /// The case this fixture exists for: the maker is allowed by the root and DENIED by
    /// <c>review</c>. Same caller, same instance, different state — and the answer flips. If state
    /// queryRoles were merged with the root's instead of replacing them, the root's ALLOW would win.
    /// </summary>
    [Fact]
    public async Task Review_DeniesTheMakerTheRootAllowed()
    {
        var instanceId = await StartCaseAsync("state-override");
        Assert.True(await MayReadAsync(instanceId, Maker), "the maker reads in intake (root ALLOW)");

        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);

        Assert.False(await MayReadAsync(instanceId, Maker), "review's own queryRoles deny the maker");
    }

    [Fact]
    public async Task Review_AllowsTheApproverAndTheAuditor()
    {
        var instanceId = await StartCaseInReviewAsync("state-allow");

        foreach (var role in new[] { Approver, Auditor })
            Assert.True(await MayReadAsync(instanceId, role), $"{role} is granted by review's queryRoles");
    }

    /// <summary>
    /// <c>escalated</c> narrows to a single ALLOW. The approver — who could read the case one state
    /// earlier and who triggered the escalation — is now refused. The tightest rule in the fixture
    /// and the one most likely to break silently if state resolution ever fell back to the root set
    /// when a state's own set is present but does not match.
    /// </summary>
    [Fact]
    public async Task Escalated_AllowsOnlyTheAuditor()
    {
        var instanceId = await StartCaseInReviewAsync("escalated-gate", actSub: StarterActor);

        // escalate is granted to $InstanceStarter, so the starter's identity has to travel with it.
        var (status, body) = await SendRawAsync(
            HttpMethod.Patch,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/transitions/escalate?sync=false",
            new { },
            HeadersFor(Approver, StarterActor));
        Assert.True((int)status < 400, $"escalate was refused with {(int)status}: {body}");
        await WaitForInstanceStateAsync(Workflow, instanceId, "escalated", Auditor);

        Assert.True(await MayReadAsync(instanceId, Auditor));
        foreach (var role in new[] { Maker, Approver, Viewer })
            Assert.False(await MayReadAsync(instanceId, role), $"escalated admits only the auditor, not {role}");
    }

    // ── multi-role callers ───────────────────────────────────────────────────

    /// <summary>
    /// A role that is merely ABSENT from an allowlist does not count against the caller: the
    /// viewer matches nothing in the root set, the approver matches an ALLOW, and the caller reads.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingAnAllowedRoleBesideAnUngrantedOne_Reads()
    {
        var instanceId = await StartCaseAsync("multi-role");

        Assert.True(await MayReadAsync(instanceId, $"{Viewer},{Approver}"));
    }

    /// <summary>
    /// An EXPLICIT deny is different: in <c>review</c> the maker is denied, and a caller holding
    /// maker AND approver is refused — a denied role is not bought back by an allowed one
    /// (three-valued evaluation, vnext #1057). Until 2026-10-05 this test asserted the opposite
    /// ("one ALLOW is enough"), which was the pre-#1057 evaluator; it matches the field-level rule
    /// (<see cref="SchemaFieldVisibilityTests"/>) now.
    /// </summary>
    [Fact]
    public async Task InReview_ACallerHoldingBothMakerAndApprover_IsRefused()
    {
        var instanceId = await StartCaseInReviewAsync("multi-role-review");

        Assert.False(await MayReadAsync(instanceId, $"{Maker},{Approver}"));
        Assert.True(await MayReadAsync(instanceId, Approver), "the approver alone still reads");
    }

    // ── the read functions themselves are not gated ─────────────────────────

    /// <summary>
    /// The other half of the contract: every read function answers every caller — including the
    /// ones <c>authorize</c> refuses above — in both states. A 403 here means the runtime gate came
    /// back and the gateway's decision is being taken twice.
    /// </summary>
    [Fact]
    public async Task ReadFunctions_DoNotEnforceQueryRoles()
    {
        var instanceId = await StartCaseAsync("reads-ungated");
        var callers = new[] { Maker, Approver, Auditor, Viewer, NoRole };

        await AssertEveryReadAnswersAsync(instanceId, "intake", IntakeReads, callers);

        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);

        await AssertEveryReadAnswersAsync(instanceId, "review", ReviewReads, callers);
    }

    private async Task AssertEveryReadAnswersAsync(
        string instanceId, string stateKey, IEnumerable<string> functions, IEnumerable<string?> callers)
    {
        foreach (var function in functions)
        foreach (var caller in callers)
        {
            var (status, _) = await CallInstanceFunctionAsync(instanceId, function, caller);
            Assert.True(status == HttpStatusCode.OK,
                $"'{function}' answered {(int)status} to {caller ?? "<no role>"} in {stateKey}; " +
                "read functions do not enforce queryRoles (authorize?queryRoles=true decides)");
        }
    }
}
