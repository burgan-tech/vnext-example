using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.RuleSchemaLab;

/// <summary>
/// Selection by the caller's resolved roles (<c>context.Caller</c>, vnext script-caller-roles). The
/// <c>review</c> state of <c>rule-schema-lab</c> picks its view by role and declares <c>submit-by-role</c>,
/// whose schema is corporate for <c>rule-schema.approver</c> and the retail fallback for everyone else.
/// <para>
/// The header-sourced tests run under both providers (default: the gateway <c>role</c> header; morph-idm:
/// a non-blank <c>role</c> header replaces the identity service's answer). The provider tests run only when
/// <c>VNEXT_CALLER_ROLE_PROVIDER=morph-idm</c> says the runtime was started with
/// <c>CallerRoleProvider__Provider=morph-idm</c> and MockLab's <c>morph-idm-roles-collection</c> seed.
/// </para>
/// </summary>
public class RuleSchemaLabCallerRolesTests : WorkflowTestBase
{
    private const string Workflow = "rule-schema-lab";
    private const string Approver = "rule-schema.approver";

    private static readonly object CorporateBody = new { taxNumber = "1234567890" };
    private static readonly object RetailBody = new { nationalId = "12345678901" };

    private readonly HttpClient _client;

    public RuleSchemaLabCallerRolesTests(VNextTestEnvironment environment) : base(environment)
    {
        _client = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };
    }

    private static bool ProviderIsMorphIdm =>
        string.Equals(
            System.Environment.GetEnvironmentVariable("VNEXT_CALLER_ROLE_PROVIDER"), "morph-idm",
            StringComparison.OrdinalIgnoreCase);

    // ── role header (both providers) ─────────────────────────────────────────

    [Fact]
    public async Task RoleHeader_Approver_SelectsTheCorporateSchema()
    {
        var instanceId = await StartReviewAsync();
        var caller = WithRole(Approver);

        var (wrong, wrongBody) = await PatchAsync(instanceId, "submit-by-role", RetailBody, caller);
        Assert.True(wrong == HttpStatusCode.BadRequest, $"retail body for an approver answered {(int)wrong}: {wrongBody}");

        var (status, body) = await PatchAsync(instanceId, "submit-by-role", CorporateBody, caller);
        Assert.True((int)status < 400, $"approver submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    [Fact]
    public async Task RoleHeader_OtherRole_FallsBackToTheRetailSchema()
    {
        var instanceId = await StartReviewAsync();

        var (status, body) = await PatchAsync(instanceId, "submit-by-role", RetailBody, WithRole("rule-schema.viewer"));
        Assert.True((int)status < 400, $"viewer submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    [Fact]
    public async Task RoleHeader_SchemaAndViewFunctions_FollowTheRole()
    {
        var instanceId = await StartReviewAsync();

        var (approverSchema, approverTag, approverSchemaBody) = await GetAsync(instanceId, "schema?transitionKey=submit-by-role", WithRole(Approver));
        Assert.True(approverSchema == HttpStatusCode.OK, $"approver schema answered {(int)approverSchema}: {approverSchemaBody}");
        Assert.Contains("rule-schema-lab-corporate", approverSchemaBody);

        var (viewerSchema, viewerTag, viewerSchemaBody) = await GetAsync(instanceId, "schema?transitionKey=submit-by-role", WithRole("rule-schema.viewer"));
        Assert.True(viewerSchema == HttpStatusCode.OK, $"viewer schema answered {(int)viewerSchema}: {viewerSchemaBody}");
        Assert.Contains("rule-schema-lab-retail", viewerSchemaBody);
        Assert.NotEqual(approverTag, viewerTag);

        // The approver's ETag never revalidates another role's selection.
        var (crossed, _, crossedBody) = await GetAsync(instanceId, "schema?transitionKey=submit-by-role", WithRole("rule-schema.viewer"), approverTag);
        Assert.True(crossed == HttpStatusCode.OK, $"viewer with the approver's ETag answered {(int)crossed}: {crossedBody}");
        Assert.Contains("rule-schema-lab-retail", crossedBody);

        var (approverView, _, approverViewBody) = await GetAsync(instanceId, "view", WithRole(Approver));
        Assert.True(approverView == HttpStatusCode.OK, $"approver view answered {(int)approverView}: {approverViewBody}");
        Assert.Contains("rule-schema-lab-approver-view", approverViewBody);

        var (viewerView, _, viewerViewBody) = await GetAsync(instanceId, "view", WithRole("rule-schema.viewer"));
        Assert.True(viewerView == HttpStatusCode.OK, $"viewer view answered {(int)viewerView}: {viewerViewBody}");
        Assert.Contains("rule-schema-lab-default-view", viewerViewBody);
    }

    // ── morph-idm provider ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task MorphIdm_ResolvedApprover_SelectsTheCorporateSchemaAndView()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var instanceId = await StartReviewAsync();
        var caller = AsIdmUser("rbs-idm-approver");

        var (view, _, viewBody) = await GetAsync(instanceId, "view", caller);
        Assert.True(view == HttpStatusCode.OK, $"approver view answered {(int)view}: {viewBody}");
        Assert.Contains("rule-schema-lab-approver-view", viewBody);

        var (wrong, wrongBody) = await PatchAsync(instanceId, "submit-by-role", RetailBody, caller);
        Assert.True(wrong == HttpStatusCode.BadRequest, $"retail body for an IDM approver answered {(int)wrong}: {wrongBody}");

        var (status, body) = await PatchAsync(instanceId, "submit-by-role", CorporateBody, caller);
        Assert.True((int)status < 400, $"IDM approver submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    [SkippableFact]
    public async Task MorphIdm_ResolvedOtherRole_FallsBack()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var instanceId = await StartReviewAsync();

        var (status, body) = await PatchAsync(instanceId, "submit-by-role", RetailBody, AsIdmUser("rbs-idm-plain"));
        Assert.True((int)status < 400, $"IDM viewer submit answered {(int)status}: {body}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    /// <summary>
    /// The identity service fails: every selection whose rule reads roles fails with 503
    /// Authorization:110006 — the transition, the schema function and the view function — instead of serving
    /// the fallback; the instance does not move, and a selection whose rules do not read roles still works.
    /// </summary>
    [SkippableFact]
    public async Task MorphIdm_ProviderDown_RoleSelectionsFail_NeverTheFallback()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var instanceId = await StartReviewAsync();
        var broken = AsIdmUser("idm-broken");

        var (submit, submitBody) = await PatchAsync(instanceId, "submit-by-role", RetailBody, broken);
        Assert.True(submit == HttpStatusCode.ServiceUnavailable, $"submit during the outage answered {(int)submit}: {submitBody}");
        Assert.Contains("Authorization:110006", submitBody);

        var (schema, _, schemaBody) = await GetAsync(instanceId, "schema?transitionKey=submit-by-role", broken);
        Assert.True(schema == HttpStatusCode.ServiceUnavailable, $"schema during the outage answered {(int)schema}: {schemaBody}");
        Assert.Contains("Authorization:110006", schemaBody);

        var (view, _, viewBody) = await GetAsync(instanceId, "view", broken);
        Assert.True(view == HttpStatusCode.ServiceUnavailable, $"view during the outage answered {(int)view}: {viewBody}");
        Assert.Contains("Authorization:110006", viewBody);

        await AssertSettledInAsync(instanceId, "review", "A");

        // A rule that reads only headers is untouched by the outage.
        var segment = AsIdmUser("idm-broken");
        segment["x-segment"] = "retail";
        var (plain, plainBody) = await PatchAsync(instanceId, "submit", RetailBody, segment);
        Assert.True((int)plain < 400, $"header-only rule during the outage answered {(int)plain}: {plainBody}");
        await AssertSettledInAsync(instanceId, "done", "C");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> StartReviewAsync()
    {
        var instanceId = await StartAsync(Workflow, new { testId = $"caller-roles-{Guid.NewGuid():N}"[..32] });
        await AssertSettledInAsync(instanceId, "review", "A");
        return instanceId;
    }

    private static Dictionary<string, string> WithRole(string role)
    {
        var headers = Headers();
        headers["role"] = role;
        return headers;
    }

    /// <summary>
    /// The caller's identity as the gateway forwards it. <c>act_sub</c> is required: without it (or
    /// <c>client_id</c>) the runtime never asks morph-idm, and the MockLab seed is keyed on <c>sub</c>.
    /// No <c>role</c> header: under morph-idm it would replace the identity service's answer.
    /// </summary>
    private static Dictionary<string, string> AsIdmUser(string user)
    {
        var headers = Headers();
        headers["sub"] = user;
        headers["act_sub"] = user;
        return headers;
    }

    private Task<(HttpStatusCode Status, string Body)> PatchAsync(
        string instanceId, string transitionKey, object body, Dictionary<string, string> headers) =>
        SendRawAsync(
            HttpMethod.Patch,
            $"api/v1/{TestDomain.Name}/workflows/{Workflow}/instances/{instanceId}/transitions/{transitionKey}?sync=false",
            body,
            headers);

    private async Task<(HttpStatusCode Status, string? ETag, string Body)> GetAsync(
        string instanceId, string functionAndQuery, Dictionary<string, string> headers, string? ifNoneMatch = null)
    {
        var url = $"api/v1/{TestDomain.Name}/workflows/{Workflow}/instances/{instanceId}/functions/{functionAndQuery}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        using var response = await _client.SendAsync(request);
        var etag = response.Headers.ETag?.ToString()
                   ?? (response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null);
        return (response.StatusCode, etag, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertSettledInAsync(string instanceId, string expectedState, string expectedStatus)
    {
        await WaitForInstanceStateAsync(Workflow, instanceId, expectedState, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Workflow, instanceId);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal(expectedState, state);
        Assert.Equal(expectedStatus, status);
        await AssertNotFaultedAsync(Workflow, instanceId);
    }
}
