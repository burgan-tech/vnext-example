using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RoleMatrixLab;

/// <summary>
/// Custom function invocation is NOT role-gated by the runtime.
/// <para>
/// <c>role-matrix-summary</c> declares <c>roles</c>, and those grants are real — <c>authorize</c>
/// evaluates them and answers 403 for a caller they exclude. But invoking the function succeeds for
/// that same caller, because deciding whether to offer or block a custom function belongs to the
/// middle tier, not the engine. vNext's job is to expose visibility and the <c>authorize</c> answer.
/// </para>
/// <para>
/// This split is deliberate and easy to mistake for a bug in either direction, so both halves are
/// pinned together in one test class. If someone reinstates the execution-time role gate, the
/// <c>Invoke_*</c> tests fail; if someone removes <c>roles</c> from the authorize path because
/// "it isn't enforced anyway", the <c>Authorize_*</c> test fails.
/// </para>
/// <para>
/// What DOES still gate the call is <c>scope</c>: <c>role-matrix-summary</c> is instance-scoped, so
/// it cannot be called on the domain route at all.
/// </para>
/// </summary>
public class CustomFunctionAuthorizationTests : RoleMatrixLabTestBase
{
    private const string Function = "role-matrix-summary";

    public CustomFunctionAuthorizationTests(VNextTestEnvironment environment) : base(environment) { }

    // ── invocation is not role-gated ─────────────────────────────────────────

    /// <summary>
    /// The maker matches no ALLOW in the function's allowlist, and invoking it still succeeds.
    /// </summary>
    [Fact]
    public async Task Invoke_SucceedsForACallerTheFunctionsRolesExclude()
    {
        var instanceId = await StartCaseAsync("custom-fn-maker");

        var (status, body) = await CallInstanceFunctionAsync(instanceId, Function, Maker);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(HasExecuted(body), $"the function answered 200 but produced no result: {body}");
    }

    /// <summary>
    /// The viewer is EXPLICITLY denied by the function's grants — the strongest form of exclusion —
    /// and invoking it still succeeds. A DENY grant that stopped execution would mean the gate was
    /// never really removed, only weakened.
    /// </summary>
    [Fact]
    public async Task Invoke_SucceedsForAnExplicitlyDeniedCaller()
    {
        var instanceId = await StartCaseAsync("custom-fn-viewer");

        var (status, _) = await CallInstanceFunctionAsync(instanceId, Function, Viewer);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Invoke_SucceedsForACallerWithNoRolesAtAll()
    {
        var instanceId = await StartCaseAsync("custom-fn-no-role");

        var (status, _) = await CallInstanceFunctionAsync(instanceId, Function, NoRole);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Invoke_SucceedsForAGrantedCallerToo()
    {
        var instanceId = await StartCaseAsync("custom-fn-approver");

        var (status, body) = await CallInstanceFunctionAsync(instanceId, Function, Approver);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(HasExecuted(body), $"the function answered 200 but produced no result: {body}");
    }

    // ── the two surfaces disagree, on purpose ────────────────────────────────

    /// <summary>
    /// The whole point, in one test: the same caller, the same function, one call each. Invocation
    /// says 200 and <c>authorize</c> says 403, and both are correct.
    /// </summary>
    [Fact]
    public async Task InvocationSucceeds_WhileAuthorizeDenies_ForTheSameCaller()
    {
        var instanceId = await StartCaseAsync("custom-fn-split");

        var (invokeStatus, _) = await CallInstanceFunctionAsync(instanceId, Function, Maker);
        var (authorizeStatus, _) = await AuthorizeAsync(instanceId, Maker, functionKey: Function);

        Assert.Equal(HttpStatusCode.OK, invokeStatus);
        Assert.Equal(HttpStatusCode.Forbidden, authorizeStatus);
    }

    // ── scope is still enforced ──────────────────────────────────────────────

    /// <summary>
    /// Scope is a statement about the SHAPE of the call, not about the caller's authority, and it is
    /// still enforced. An instance-scoped function has no meaning without an instance, so the
    /// domain route must refuse it regardless of who is asking.
    /// </summary>
    [Fact]
    public async Task InstanceScopedFunction_IsRefusedOnTheDomainRoute()
    {
        var (status, _) = await SendRawAsync(
            HttpMethod.Get, $"api/v1/core/functions/{Function}", headers: HeadersFor(Approver));

        Assert.True((int)status >= 400,
            $"an instance-scoped function answered {(int)status} on the domain route");
    }

    // ── discovery surfaces ───────────────────────────────────────────────────

    /// <summary>
    /// The <c>/info</c> contract surface is scope-gated, not role-gated. A caller the function's
    /// grants exclude still receives its contract, because the middle tier — which owns the
    /// decision — needs to know the function exists in order to ask <c>authorize</c> about it.
    /// <para>
    /// This is the discovery half of the same removal: execution and discovery must not drift, so a
    /// change that reinstates the role gate on one of them without the other fails here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Info_IsReturnedToACallerTheFunctionsRolesExclude()
    {
        var instanceId = await StartCaseAsync("custom-fn-info");

        var (status, body) = await SendRawAsync(
            HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/{Function}/info",
            headers: HeadersFor(Maker));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(Function, body);
    }

    /// <summary>
    /// Same surface, explicitly denied caller. Pinned separately because a partial reinstatement
    /// that honoured DENY grants but ignored allowlists would still pass the test above.
    /// </summary>
    [Fact]
    public async Task Info_IsReturnedToAnExplicitlyDeniedCaller()
    {
        var instanceId = await StartCaseAsync("custom-fn-info-deny");

        var (status, _) = await SendRawAsync(
            HttpMethod.Get,
            $"api/v1/core/workflows/{Workflow}/instances/{instanceId}/functions/{Function}/info",
            headers: HeadersFor(Viewer));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static bool HasExecuted(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return false;

        // The function's own payload lands under `data` for a non-raw response; tolerate either
        // shape so the assertion survives an envelope change it is not trying to test.
        if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return data.TryGetProperty("executed", out _) || data.TryGetProperty("caseRef", out _);

        return body.TryGetProperty("executed", out _) || body.TryGetProperty("caseRef", out _);
    }
}
