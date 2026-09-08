using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using VNext.Testing.Sdk.Client;

namespace Core.IntegrationTests.Tests.CrossDomainLab;

/// <summary>
/// Shared plumbing for the cross-domain lab: a second <see cref="VNextApiClient"/> pointed at the
/// <c>partner</c> orchestrator, raw function/authorize readers against the <c>core</c> parent, and the
/// step helpers that walk <c>xd-parent</c> through its chain.
/// </summary>
/// <remarks>
/// Every test starts its own parent with a fresh <c>testId</c>; the partner instances it creates are
/// found either through the parent's correlations (SubFlow) or through the ids the parent's task
/// mappings wrote into instance data (SubProcess / Start). Nothing is looked up by list.
/// </remarks>
public abstract class CrossDomainLabTestBase : WorkflowTestBase
{
    protected const string Parent = "xd-parent";
    protected const string Child = "xd-child";
    protected const string Remote = "xd-remote";
    protected const string Worker = "xd-worker";

    /// <summary>The only role allowed on the partner child's <c>child-approve</c> transition.</summary>
    protected const string Approver = "xd-approver";
    /// <summary>A role the child grants nothing to — authorize must answer 403 for it.</summary>
    protected const string Viewer = "xd-viewer";

    protected readonly CrossDomainLabFixture Lab;
    private readonly HttpClient _coreRaw;
    private readonly VNextApiClient? _partnerApi;

    protected CrossDomainLabTestBase(VNextTestEnvironment environment, CrossDomainLabFixture lab) : base(environment)
    {
        Lab = lab;
        _coreRaw = new HttpClient { BaseAddress = new Uri(environment.OrchestratorBaseUrl.TrimEnd('/') + "/") };

        if (lab.PartnerBaseUrl is not null)
        {
            _partnerApi = new VNextApiClient(new VNextApiClientOptions
            {
                BaseUrl = lab.PartnerBaseUrl,
                Domain = "partner"
            });
        }
    }

    /// <summary>Partner-domain client. Call <see cref="RequirePartner"/> first.</summary>
    protected VNextApiClient PartnerApi =>
        _partnerApi ?? throw new InvalidOperationException("partner client unavailable — VNEXT_PARTNER_BASE_URL is not set");

    /// <summary>
    /// Skips the test when the lab's partner domain is not configured (containerized single-domain
    /// runs). The lab is documented in labs/cross-domain/README.md.
    /// </summary>
    protected void RequirePartner() =>
        Skip.If(Lab.PartnerBaseUrl is null,
            "VNEXT_PARTNER_BASE_URL is not set — the cross-domain lab (labs/cross-domain/lab.sh up) is required.");

    // ── parent steps ─────────────────────────────────────────────────────────

    protected static string NewTestId(string tag) => $"{tag}-{Guid.NewGuid():N}"[..24];

    /// <summary>Starts xd-parent and waits for it to park in xd-hub.</summary>
    protected async Task<(string ParentId, string TestId)> StartParentAsync(string tag)
    {
        var testId = NewTestId(tag);
        var parentId = await StartAsync(Parent, new { testId });
        await WaitForInstanceStateAsync(Parent, parentId, "xd-hub");
        return (parentId, testId);
    }

    /// <summary>
    /// Fires enter-subflow and waits until the state function reports the partner child's
    /// <c>child-review</c> state. The parent itself stays Busy for the whole subflow lifetime by
    /// design, so this waits on the OBSERVED (leaf) state, never on the parent status.
    /// Returns the partner child's instance id from the parent's active correlations.
    /// </summary>
    protected async Task<string> EnterSubflowAsync(string parentId)
    {
        var status = await RunAsync(Parent, parentId, "enter-subflow", new { });
        Assert.True((int)status < 400, $"enter-subflow was refused with {(int)status}");

        await WaitForObservedStateAsync(Parent, parentId, "child-review", timeout: TimeSpan.FromSeconds(60));

        var subflows = await GetActiveSubflowsAsync(Parent, parentId);
        Assert.True(subflows.TryGetValue(Child, out var childId),
            $"parent {parentId} has no active correlation for '{Child}': {string.Join(",", subflows.Keys)}");
        return childId!;
    }

    /// <summary>Approves the child THROUGH THE PARENT (forward) and waits for the parent to resume.</summary>
    protected async Task ApproveChildThroughParentAsync(string parentId, string approvedBy = "xd-tester")
    {
        var status = await RunAsync(Parent, parentId, "child-approve", new { approvedBy }, Approver);
        Assert.True((int)status < 400, $"child-approve forward was refused with {(int)status}");
        await WaitForInstanceStateAsync(Parent, parentId, "xd-after-subflow", timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>Start → enter subflow → approve → parent in xd-after-subflow. The trigger-task chain starts here.</summary>
    protected async Task<(string ParentId, string TestId, string ChildId)> BringParentPastSubflowAsync(string tag)
    {
        var (parentId, testId) = await StartParentAsync(tag);
        var childId = await EnterSubflowAsync(parentId);
        await ApproveChildThroughParentAsync(parentId);
        return (parentId, testId, childId);
    }

    /// <summary>Runs one parent transition and waits for the parent to reach the expected state (fails fast on Faulted).</summary>
    protected async Task StepAsync(string parentId, string transitionKey, string expectedState)
    {
        var status = await RunAsync(Parent, parentId, transitionKey, new { });
        Assert.True((int)status < 400, $"'{transitionKey}' was refused with {(int)status}");
        await WaitForInstanceStateAsync(Parent, parentId, expectedState, timeout: TimeSpan.FromSeconds(60));
    }

    // ── core raw reads (status preserved) ────────────────────────────────────

    protected async Task<(HttpStatusCode Status, JsonElement Body)> CallParentFunctionAsync(
        string parentId, string function, string? roles = null, string? query = null)
    {
        var url = $"api/v1/core/workflows/{Parent}/instances/{parentId}/functions/{function}"
                  + (query is null ? "" : "?" + query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in Headers(roles)) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _coreRaw.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, Parse(body));
    }

    protected Task<(HttpStatusCode Status, JsonElement Body)> AuthorizeAsync(string parentId, string? roles, string transitionKey) =>
        CallParentFunctionAsync(parentId, "authorize", roles, $"transitionKey={transitionKey}");

    // ── partner reads ────────────────────────────────────────────────────────

    /// <summary>GET the partner instance; returns (status, currentState, status letter, attributes).</summary>
    protected async Task<(HttpStatusCode Http, string State, string Status, JsonElement Attributes)> GetPartnerInstanceAsync(
        string workflow, string identifier)
    {
        var response = await PartnerApi.GetInstanceAsync(workflow, identifier, Headers());
        if ((int)response.StatusCode >= 400)
            return (response.StatusCode, "", "", default);

        var metadata = response.Body.GetProperty("metadata");
        var attributes = response.Body.TryGetProperty("attributes", out var a) ? a : default;
        return (response.StatusCode,
                metadata.GetProperty("currentState").GetString() ?? "",
                metadata.GetProperty("status").GetString() ?? "",
                attributes);
    }

    protected Task WaitForPartnerStateAsync(string workflow, string identifier, string state, TimeSpan? timeout = null) =>
        WaitUntilAsync(async () =>
        {
            var (http, current, status, _) = await GetPartnerInstanceAsync(workflow, identifier);
            if ((int)http >= 400) return false;
            Assert.NotEqual("F", status);
            return current == state;
        }, $"partner {workflow}/{identifier} never reached '{state}'", timeout ?? TimeSpan.FromSeconds(60));

    protected static JsonElement Parse(string body)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    protected static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
