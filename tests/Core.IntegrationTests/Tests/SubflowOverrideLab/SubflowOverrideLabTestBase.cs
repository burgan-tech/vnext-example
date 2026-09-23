using System.Net;
using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.SubflowOverrideLab;

/// <summary>
/// Shared plumbing for the <c>subflow-override-lab</c> suite: a parent's
/// <c>overrides.states.&lt;childState&gt;.interaction.longPoll</c> and scoped <c>views</c> overrides,
/// resolved CHILD-side on the child's own state.
/// <para>
/// Every flow walks itself to its waiting state, so a test starts the top instance and waits for the
/// correlation. Reads go out over <see cref="WorkflowTestBase.SendRawAsync"/> because several
/// assertions are about a status code (<c>authorize</c> answers 403 with the verdict in the body).
/// </para>
/// </summary>
public abstract class SubflowOverrideLabTestBase : WorkflowTestBase
{
    protected const string Child = "subflow-override-lab-child";
    protected const string PlainChild = "subflow-override-lab-plain-child";
    protected const string Mid = "subflow-override-lab-mid";

    /// <summary>Duration-only override (120 s) + state- and transition-scoped view overrides.</summary>
    protected const string Parent = "subflow-override-lab-parent";
    /// <summary>Duration-only override of 5 s — the behavioural probe for the armed window.</summary>
    protected const string ParentShort = "subflow-override-lab-parent-short";
    /// <summary>Roles-only override ([ovr.parent-ack]) + a view override pointing at a view that does not exist.</summary>
    protected const string ParentRoles = "subflow-override-lab-parent-roles";
    /// <summary>A long-poll override on a child state that declares no long-poll.</summary>
    protected const string ParentNoLongPoll = "subflow-override-lab-parent-nolp";
    /// <summary>The deprecated parent-side <c>overrides.views</c> map.</summary>
    protected const string ParentLegacy = "subflow-override-lab-parent-legacy";
    /// <summary>TOP → MID → CHILD; TOP's override names a state that exists only in the grandchild.</summary>
    protected const string Top = "subflow-override-lab-top";

    /// <summary>The child's OWN long-poll role.</summary>
    protected const string ChildAck = "ovr.child-ack";
    /// <summary>Granted only by a parent's roles override.</summary>
    protected const string ParentAck = "ovr.parent-ack";

    /// <summary>The child's declared window; every override below is far from it.</summary>
    protected const int ChildWindowSeconds = 600;

    protected const string ChildLpView = "subflow-override-lab-child-lp-view";
    protected const string ChildConfirmView = "subflow-override-lab-child-confirm-view";
    protected const string ChildPlainView = "subflow-override-lab-child-plain-view";
    protected const string ParentLpView = "subflow-override-lab-parent-lp-view";
    protected const string ParentConfirmView = "subflow-override-lab-parent-confirm-view";
    protected const string LegacyView = "subflow-override-lab-legacy-view";

    protected SubflowOverrideLabTestBase(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// Starts <paramref name="workflow"/> as <paramref name="roles"/> and returns the id of the
    /// instance correlated as <paramref name="childWorkflow"/> beneath it.
    /// <para>
    /// The starter's roles matter: the long-poll arm (order 75) pauses only when the TRIGGERING
    /// caller satisfies the effective long-poll roles, and the lab's SubFlow mapping forwards the
    /// caller's role headers to the child for exactly that reason.
    /// </para>
    /// </summary>
    protected async Task<(string ParentId, string ChildId)> StartPairAsync(
        string workflow, string childWorkflow, string roles)
    {
        var parentId = await StartAsync(workflow, new { labRef = $"ovr-{Guid.NewGuid():N}"[..20] }, roles);
        await AssertNotFaultedAsync(workflow, parentId, roles);

        string? childId = null;
        await WaitUntilAsync(async () =>
        {
            var subs = await GetActiveSubflowsAsync(workflow, parentId, roles);
            return subs.TryGetValue(childWorkflow, out childId);
        }, $"{workflow} {parentId} should open a correlation to {childWorkflow}");

        return (parentId, childId!);
    }

    /// <summary>Waits until the leaf the parent observes has reached <paramref name="state"/>.</summary>
    protected Task WaitForLeafStateAsync(string workflow, string instanceId, string state, string roles) =>
        WaitUntilAsync(async () =>
        {
            var body = await StateBodyAsync(workflow, instanceId, roles);
            return body.GetProperty("state").GetString() == state;
        }, $"{workflow} {instanceId} should observe {state}");

    /// <summary>The state function body as <paramref name="roles"/> sees it (asserting 200).</summary>
    protected async Task<JsonElement> StateBodyAsync(string workflow, string instanceId, string? roles)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/state", headers: Headers(roles));
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The <c>interaction</c> block, or null when the body carries none.</summary>
    protected async Task<JsonElement?> InteractionAsync(string workflow, string instanceId, string? roles)
    {
        var body = await StateBodyAsync(workflow, instanceId, roles);
        return body.TryGetProperty("interaction", out var interaction) && interaction.ValueKind == JsonValueKind.Object
            ? interaction
            : null;
    }

    /// <summary>
    /// <c>authorize?ack=true</c>: true on 200, false on 403, and the body's <c>allowed</c> must agree.
    /// Anything else fails — a 404 or 500 must not be read as a refusal.
    /// </summary>
    protected async Task<bool> AckAllowedAsync(string workflow, string instanceId, string? roles)
    {
        var (status, raw) = await SendRawAsync(HttpMethod.Get,
            $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/authorize?ack=true",
            headers: Headers(roles));

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"authorize?ack=true answered {(int)status}: {raw}");

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var body = JsonDocument.Parse(raw).RootElement;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("allowed", out var allowed))
                Assert.Equal(status == HttpStatusCode.OK, allowed.GetBoolean());
        }

        return status == HttpStatusCode.OK;
    }

    /// <summary>The key of the view the view function served.</summary>
    protected async Task<string?> ViewKeyAsync(
        string workflow, string instanceId, string roles, string? transitionKey = null)
    {
        var url = $"api/v1/core/workflows/{workflow}/instances/{instanceId}/functions/view"
                  + (transitionKey is null ? "" : $"?transitionKey={transitionKey}");
        var (status, body) = await SendRawAsync(HttpMethod.Get, url, headers: Headers(roles));
        Assert.True(status == HttpStatusCode.OK, $"view function answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("key").GetString();
    }
}
