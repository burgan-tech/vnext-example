using System.Globalization;
using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.CallerRolesLab;

/// <summary>
/// <c>context.Caller</c> outside request-bound selection (vnext script-caller-roles, council
/// 2026-11-12-script-caller-roles-background, Chair override): an automatic rule, a timer script (computed at
/// arming) and a SubFlow output mapping (the child settler's identity). Header-sourced tests run under both
/// providers; the identity-service tests only when <c>VNEXT_CALLER_ROLE_PROVIDER=morph-idm</c>.
/// </summary>
public class CallerRolesLabTests : WorkflowTestBase
{
    private const string Approver = "rule-schema.approver";
    private const string Auto = "caller-roles-auto-lab";
    private const string Timer = "caller-roles-timer-lab";
    private const string Parent = "caller-roles-parent-lab";

    public CallerRolesLabTests(VNextTestEnvironment environment) : base(environment) { }

    private static bool ProviderIsMorphIdm =>
        string.Equals(System.Environment.GetEnvironmentVariable("VNEXT_CALLER_ROLE_PROVIDER"), "morph-idm",
            StringComparison.OrdinalIgnoreCase);

    // ── automatic rule ───────────────────────────────────────────────────────

    [Fact]
    public async Task AutoRule_Approver_TakesTheRoleRule()
    {
        var id = await StartAsAsync(Auto, WithRole(Approver));
        await AssertSettledInAsync(Auto, id, "approved", "C");
    }

    [Fact]
    public async Task AutoRule_OtherRole_TakesTheDefaultAutoTransition()
    {
        var id = await StartAsAsync(Auto, WithRole("rule-schema.viewer"));
        await AssertSettledInAsync(Auto, id, "standard", "C");
    }

    [SkippableFact]
    public async Task AutoRule_MorphIdmApprover_TakesTheRoleRule()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var id = await StartAsAsync(Auto, AsIdmUser("rbs-idm-approver"));
        await AssertSettledInAsync(Auto, id, "approved", "C");
    }

    /// <summary>The provider fails while the rule reads roles: the DefaultAutoTransition must NOT be taken.</summary>
    [SkippableFact]
    public async Task AutoRule_ProviderDown_NeverFallsThroughToTheDefault()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var id = await StartAsAsync(Auto, AsIdmUser("idm-broken"));
        await WaitUntilSettledAsync(Auto, id);

        var (state, status) = await GetInstanceStateAsync(Auto, id);
        Assert.NotEqual("standard", state);
        Assert.NotEqual("approved", state);
        Assert.True(status is "F" or "A", $"expected a failed or resting gate, got {state}/{status}");
    }

    // ── timer script (arming) ────────────────────────────────────────────────

    [Fact]
    public async Task Timer_Approver_ArmsTheLongWindow()
    {
        var id = await StartAsAsync(Timer, WithRole(Approver));
        await AssertSettledInAsync(Timer, id, "wait", "A");
        await AssertArmedInAsync(id, TimeSpan.FromHours(48));
    }

    [Fact]
    public async Task Timer_OtherRole_ArmsTheShortWindow()
    {
        var id = await StartAsAsync(Timer, WithRole("rule-schema.viewer"));
        await AssertSettledInAsync(Timer, id, "wait", "A");
        await AssertArmedInAsync(id, TimeSpan.FromHours(24));
    }

    // ── SubFlow output mapping (child settler) ───────────────────────────────

    [Theory]
    [InlineData(Approver, true)]
    [InlineData("rule-schema.viewer", false)]
    public async Task SubFlowOutputMapping_SeesTheChildSettlersRole(string settlerRole, bool expected)
    {
        var id = await StartAsAsync(Parent, Headers());
        await WaitUntilAsync(async () => (await GetActiveSubflowsAsync(Parent, id)).Count > 0, "child started");

        var (status, body) = await PatchAsync(Parent, id, "child-complete", WithRole(settlerRole));
        Assert.True((int)status < 400, $"child-complete answered {(int)status}: {body}");

        await AssertSettledInAsync(Parent, id, "parent-done", "C");
        var attributes = await GetAttributesAsync(Parent, id);
        Assert.Equal(expected, attributes.GetProperty("settledByApprover").GetBoolean());
    }

    /// <summary>
    /// The provider fails while the output mapping reads roles: the completion is redelivered, the parent is
    /// NOT faulted and no output is persisted.
    /// </summary>
    [SkippableFact]
    public async Task SubFlowOutputMapping_ProviderDown_ParentIsNotFaulted()
    {
        Skip.IfNot(ProviderIsMorphIdm, "runtime is not running the morph-idm provider");
        var id = await StartAsAsync(Parent, Headers());
        await WaitUntilAsync(async () => (await GetActiveSubflowsAsync(Parent, id)).Count > 0, "child started");

        var (status, body) = await PatchAsync(Parent, id, "child-complete", AsIdmUser("idm-broken"));
        Assert.True((int)status < 400, $"child-complete answered {(int)status}: {body}");

        await Task.Delay(TimeSpan.FromSeconds(15));
        var (state, parentStatus) = await GetInstanceStateAsync(Parent, id);
        Assert.NotEqual("F", parentStatus);
        Assert.Equal("parent-wait", state);
        var attributes = await GetAttributesAsync(Parent, id);
        Assert.False(attributes.TryGetProperty("settledByApprover", out _), "no output may be persisted");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> StartAsAsync(string workflow, Dictionary<string, string> headers)
    {
        var (status, body) = await SendRawAsync(
            HttpMethod.Post, $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/start",
            new { testId = $"caller-roles-{Guid.NewGuid():N}"[..32] }, headers);
        Assert.True((int)status < 400, $"start of {workflow} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    private Task<(HttpStatusCode Status, string Body)> PatchAsync(
        string workflow, string instanceId, string transitionKey, Dictionary<string, string> headers) =>
        SendRawAsync(HttpMethod.Patch,
            $"api/v1/{TestDomain.Name}/workflows/{workflow}/instances/{instanceId}/transitions/{transitionKey}?sync=false",
            new { }, headers);

    private async Task AssertArmedInAsync(string instanceId, TimeSpan expected)
    {
        var executeAt = await GetScheduledExecuteAtAsync(Timer, instanceId, "expire");
        Assert.False(string.IsNullOrEmpty(executeAt), "no scheduled transition armed");
        var at = DateTimeOffset.Parse(executeAt!, CultureInfo.InvariantCulture);
        var delta = at - DateTimeOffset.UtcNow;
        Assert.InRange(delta, expected - TimeSpan.FromMinutes(10), expected + TimeSpan.FromMinutes(1));
    }

    private static Dictionary<string, string> WithRole(string role)
    {
        var headers = Headers();
        headers["role"] = role;
        return headers;
    }

    /// <summary>Identity as the gateway forwards it, no role header (it would replace the IDM answer).</summary>
    private static Dictionary<string, string> AsIdmUser(string user)
    {
        var headers = Headers();
        headers["sub"] = user;
        headers["act_sub"] = user;
        return headers;
    }

    private async Task AssertSettledInAsync(string workflow, string instanceId, string expectedState, string expectedStatus)
    {
        await WaitForInstanceStateAsync(workflow, instanceId, expectedState, timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(workflow, instanceId);
        var (state, status) = await GetInstanceStateAsync(workflow, instanceId);
        Assert.Equal(expectedState, state);
        Assert.Equal(expectedStatus, status);
    }
}
