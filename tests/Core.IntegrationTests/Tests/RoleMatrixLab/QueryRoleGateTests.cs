using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// <c>queryRoles</c> — the gate every instance READ passes through, on every built-in function.
/// <para>
/// Two rules are under test. First, the state's own <c>queryRoles</c> REPLACE the workflow root's;
/// they do not add to them. <c>role-matrix-lab</c> makes that visible by allowing the maker at the
/// root and denying it in <c>review</c>: the same caller reads the case in <c>intake</c> and is
/// refused once it moves. Second, every built-in function consults the same gate — a deployment
/// where <c>state</c> answers 403 but <c>data</c> answers 200 is leaking.
/// </para>
/// </summary>
public class QueryRoleGateTests : RoleMatrixLabTestBase
{
    public QueryRoleGateTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>The built-in read functions, all gated by the same queryRoles evaluation.</summary>
    public static TheoryData<string> ReadFunctions() => new() { "state", "data", "schema", "master", "view" };

    // ── root queryRoles ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ReadFunctions))]
    public async Task Intake_AllowsEveryRoleTheRootGrants(string function)
    {
        var instanceId = await StartCaseAsync("root-allow");

        foreach (var role in new[] { Maker, Approver, Auditor })
        {
            var (status, _) = await CallInstanceFunctionAsync(instanceId, function, role);
            Assert.True(status == HttpStatusCode.OK,
                $"'{function}' refused {role} with {(int)status} in intake, where the root queryRoles allow it");
        }
    }

    /// <summary>
    /// The root set is an allowlist: it names three roles and grants nothing else. A caller holding
    /// a role that appears nowhere in the set is refused — that is the whole point of an allowlist,
    /// and it is what separates this set from a deny-only one.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadFunctions))]
    public async Task Intake_RefusesARoleTheRootNeverGrants(string function)
    {
        var instanceId = await StartCaseAsync("root-deny");

        var (status, _) = await CallInstanceFunctionAsync(instanceId, function, Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    /// <summary>
    /// A caller with no roles at all is still evaluated — it just matches no ALLOW in an allowlist.
    /// Worth pinning separately from the viewer case: a role-less caller takes a different path
    /// through the evaluator (predefined and dynamic grants are still resolved once for it), and a
    /// regression that skips evaluation entirely would let it through.
    /// </summary>
    [Fact]
    public async Task ARoleLessCaller_IsRefused()
    {
        var instanceId = await StartCaseAsync("no-role");

        var (status, _) = await CallInstanceFunctionAsync(instanceId, "state", NoRole);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // ── state queryRoles replace the root's ──────────────────────────────────

    /// <summary>
    /// The case this fixture exists for: the maker is allowed by the root and DENIED by
    /// <c>review</c>. Same caller, same instance, different state — and the answer flips. If state
    /// queryRoles were merged with the root's instead of replacing them, the root's ALLOW would win
    /// and this would read 200.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadFunctions))]
    public async Task Review_DeniesTheMakerTheRootAllowed(string function)
    {
        var instanceId = await StartCaseAsync("state-override");

        var (inIntake, _) = await CallInstanceFunctionAsync(instanceId, function, Maker);
        Assert.Equal(HttpStatusCode.OK, inIntake);

        await RunAcceptedAsync(Workflow, instanceId, "submit-for-review", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "review", Approver);

        var (inReview, _) = await CallInstanceFunctionAsync(instanceId, function, Maker);
        Assert.Equal(HttpStatusCode.Forbidden, inReview);
    }

    [Theory]
    [MemberData(nameof(ReadFunctions))]
    public async Task Review_AllowsTheApproverAndTheAuditor(string function)
    {
        var instanceId = await StartCaseInReviewAsync("state-allow");

        foreach (var role in new[] { Approver, Auditor })
        {
            var (status, _) = await CallInstanceFunctionAsync(instanceId, function, role);
            Assert.True(status == HttpStatusCode.OK,
                $"'{function}' refused {role} with {(int)status} in review");
        }
    }

    /// <summary>
    /// <c>escalated</c> narrows to a single ALLOW. The approver — who could read the case one state
    /// earlier and who triggered the escalation — is now refused. This is the tightest gate in the
    /// fixture and the one most likely to break silently if state resolution ever falls back to the
    /// root set when a state's own set is present but does not match.
    /// </summary>
    [Fact]
    public async Task Escalated_AllowsOnlyTheAuditor()
    {
        var instanceId = await StartCaseInReviewAsync("escalated-gate", startRoles: Approver);

        await RunAcceptedAsync(Workflow, instanceId, "escalate", new { }, Approver);
        await WaitForInstanceStateAsync(Workflow, instanceId, "escalated", Auditor);

        var (auditor, _) = await CallInstanceFunctionAsync(instanceId, "state", Auditor);
        Assert.Equal(HttpStatusCode.OK, auditor);

        foreach (var role in new[] { Maker, Approver, Viewer })
        {
            var (status, _) = await CallInstanceFunctionAsync(instanceId, "state", role);
            Assert.True(status == HttpStatusCode.Forbidden,
                $"escalated let {role} read with {(int)status}; only the auditor is granted there");
        }
    }

    // ── multi-role callers ───────────────────────────────────────────────────

    /// <summary>
    /// Any allowed role wins. A caller presenting both a denied and an allowed role reads
    /// successfully, because the roles are evaluated as a set and one ALLOW is enough.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingAnAllowedRoleAmongDeniedOnes_Reads()
    {
        var instanceId = await StartCaseAsync("multi-role");

        var (status, _) = await CallInstanceFunctionAsync(instanceId, "state", $"{Viewer},{Approver}");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// DENY still wins within one role's evaluation: in <c>review</c> the maker is explicitly denied
    /// while the approver is allowed, and a caller holding both reads — the DENY binds the maker
    /// role, not the caller. This is the pair to the field-level test, where a DENY on the same role
    /// does remove a field for exactly the same caller.
    /// </summary>
    [Fact]
    public async Task InReview_ACallerHoldingBothMakerAndApprover_Reads()
    {
        var instanceId = await StartCaseInReviewAsync("multi-role-review");

        var (status, _) = await CallInstanceFunctionAsync(instanceId, "state", $"{Maker},{Approver}");

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
