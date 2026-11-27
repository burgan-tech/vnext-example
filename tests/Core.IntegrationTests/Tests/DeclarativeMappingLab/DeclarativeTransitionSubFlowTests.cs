using System.Text.Json;
using Core.IntegrationTests.Infrastructure;
using Core.IntegrationTests.Tests.ErrorBoundaryLab;

namespace Core.IntegrationTests.Tests.DeclarativeMappingLab;

/// <summary>
/// Declarative blocks outside task bindings (vnext #1229, phase C1): a transition's <c>output</c> (also on
/// the <c>startTransition</c>), a SubFlow state's <c>input</c> (the child start document
/// <c>{ attributes, headers, key, tags }</c>) and its <c>output</c> (<c>result = { data, status }</c>, default
/// select <c>$.data</c>), each in place of a C# <c>mapping</c> script. Contract: vnext
/// <c>docs/runtime/declarative-task-mappings.md</c> § "Blocks outside task bindings".
/// <para>
/// Parent <c>dml-transition-subflow</c>, child <c>dml-sf-child</c>. The child's <c>mode</c> (written by the
/// parent's input block from the hop body) decides: <c>complete</c> finishes at once, <c>wait</c> rests in
/// <c>child-wait</c>, where <c>finish</c> completes it and <c>fail</c> faults it (a task output expression
/// that fails at run time with no error boundary, vnext #1257).
/// </para>
/// </summary>
public class DeclarativeTransitionSubFlowTests : WorkflowTestBase
{
    private const string Parent = "dml-transition-subflow";
    private const string Child = "dml-sf-child";

    public DeclarativeTransitionSubFlowTests(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>
    /// The start transition's <c>output</c> starts from an EMPTY delta: only what <c>select</c>/<c>into</c> and
    /// <c>set</c> write becomes the initial data; the rest of the start attributes does not land.
    /// </summary>
    [Fact]
    public async Task StartOutput_SetsInitialData()
    {
        var instanceId = await StartAsync(Parent, new
        {
            key = NewKey("dml-start"),
            attributes = new { customer = new { id = "c-1", name = "Ada" }, noise = "must-not-land" }
        });
        await WaitUntilSettledAsync(Parent, instanceId);

        var data = await GetAttributesAsync(Parent, instanceId);
        Assert.Equal("c-1", data.GetProperty("customer").GetProperty("id").GetString());    // select + into
        Assert.Equal("Ada", data.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("start-output", data.GetProperty("startedVia").GetString());            // set
        Assert.False(data.TryGetProperty("noise", out _), "only the output delta may land on start");
        Assert.Equal(("ready", "A"), await GetInstanceStateAsync(Parent, instanceId));
    }

    /// <summary>
    /// A manual transition's <c>output</c>: the request's <c>order</c> lands under <c>$.order</c>, <c>set</c> adds
    /// <c>instance.currentState</c> (the source state, before ChangeState) and <c>now</c>; the rest of the body
    /// (<c>noise</c>) does not land, and data the instance already had is kept (deep merge).
    /// </summary>
    [Fact]
    public async Task TransitionOutput_WritesOnlyTheDelta()
    {
        var instanceId = await StartAsync(Parent, new
        {
            key = NewKey("dml-submit"),
            attributes = new { customer = new { id = "c-2", name = "Grace" } }
        });
        await WaitUntilSettledAsync(Parent, instanceId);

        await RunAcceptedAsync(Parent, instanceId, "submit",
            body: new { order = new { amount = 250, currency = "TRY" }, noise = "must-not-land" });
        Assert.Equal(("submitted", "C"), await GetInstanceStateAsync(Parent, instanceId));

        var data = await GetAttributesAsync(Parent, instanceId);
        var order = data.GetProperty("order");
        Assert.Equal(250, order.GetProperty("amount").GetInt32());
        Assert.Equal("TRY", order.GetProperty("currency").GetString());
        Assert.Equal("ready", order.GetProperty("previousState").GetString());
        Assert.True(DateTimeOffset.TryParse(order.GetProperty("submittedAt").GetString(), out _), "now must be a timestamp");
        Assert.False(data.TryGetProperty("noise", out _), "the request body outside the block must not land");
        Assert.Equal("c-2", data.GetProperty("customer").GetProperty("id").GetString()); // earlier data kept
        Assert.Equal("start-output", data.GetProperty("startedVia").GetString());
    }

    /// <summary>
    /// The SubFlow <c>input</c> block builds the child's start document: <c>attributes</c> from parent data
    /// (<c>data.customer.id</c>) and the entering hop's body (<c>input.mode</c>, <c>input.amount</c>), the
    /// <c>key</c> as a string and <c>tags</c> as <c>new string[] { … }</c>.
    /// </summary>
    [Fact]
    public async Task SubFlowInput_ChildGetsAttributesKeyTags()
    {
        var parentId = await StartParentAsync("c-3");
        var childKey = NewKey("dml-child");

        await RunAsyncAccepted(parentId, "run-child", new { mode = "wait", amount = 75, childKey });
        var childId = await WaitForChildAsync(parentId);
        await WaitForInstanceStateAsync(Child, childId, "child-wait", timeout: TimeSpan.FromSeconds(60));

        var child = (await Api.GetInstanceAsync(Child, childId, Headers())).Body;
        Assert.Equal(childKey, child.GetProperty("key").GetString());
        var tags = child.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray();
        // The runtime prepends its own lineage tags (parent.key/domain/flow, root.instance); the authored ones follow.
        Assert.Contains("dml-child", tags);
        Assert.Contains("declarative-input", tags);
        Assert.Contains(tags, t => t!.StartsWith("parent.flow:" + Parent, StringComparison.Ordinal));

        var attributes = child.GetProperty("attributes");
        Assert.Equal("c-3", attributes.GetProperty("customerId").GetString());
        Assert.Equal("wait", attributes.GetProperty("mode").GetString());
        Assert.Equal(75, attributes.GetProperty("amount").GetInt32());
        Assert.False(attributes.TryGetProperty("customer", out _), "the child gets only what the input block wrote");

        // Finish the child so the parent does not stay parked on an open correlation.
        await RunAcceptedAsync(Child, childId, "finish");
        await WaitForInstanceStateAsync(Parent, parentId, "child-settled", timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// The SubFlow <c>output</c> block on completion: <c>into</c> without <c>select</c> takes the default
    /// <c>$.data</c> (the child's data), <c>set</c> reads <c>result.status</c> = <c>completed</c> and
    /// <c>result.data.amount</c>; the parent then leaves the SubFlow state.
    /// </summary>
    [Fact]
    public async Task SubFlowOutput_Completed_WritesResultData()
    {
        var parentId = await StartParentAsync("c-4");

        await RunAsyncAccepted(parentId, "run-child", new { mode = "complete", amount = 40, childKey = NewKey("dml-child") });
        await WaitForInstanceStateAsync(Parent, parentId, "child-settled", timeout: TimeSpan.FromSeconds(60));
        await WaitUntilSettledAsync(Parent, parentId);
        Assert.Equal(("child-settled", "C"), await GetInstanceStateAsync(Parent, parentId));

        var data = await GetAttributesAsync(Parent, parentId);
        Assert.Equal("completed", data.GetProperty("childOutcome").GetString());
        Assert.Equal(40, data.GetProperty("childAmount").GetInt32());
        var child = data.GetProperty("child");
        Assert.Equal("c-4", child.GetProperty("customerId").GetString());
        Assert.Equal("complete", child.GetProperty("mode").GetString());
        Assert.Equal("c-4", data.GetProperty("customer").GetProperty("id").GetString()); // parent data kept
    }

    /// <summary>
    /// The SubFlow <c>output</c> block also runs on the FAULT path, and <c>result.status</c> tells it apart:
    /// the child faults (task output error, no boundary), the parent — no boundary either — faults, and the
    /// block's delta still lands on the parent with <c>childOutcome = "faulted"</c>.
    /// </summary>
    [SkippableFact]
    public async Task SubFlowOutput_Faulted_WritesStatus()
    {
        await SkipUnlessMockLabAsync();
        var parentId = await StartParentAsync("c-5");

        await RunAsyncAccepted(parentId, "run-child", new { mode = "wait", amount = 60, childKey = NewKey("dml-child") });
        var childId = await WaitForChildAsync(parentId);
        await WaitForInstanceStateAsync(Child, childId, "child-wait", timeout: TimeSpan.FromSeconds(60));

        await RunAcceptedAsync(Child, childId, "fail");
        Assert.Equal("F", (await GetInstanceStateAsync(Child, childId)).Status);

        await WaitUntilAsync(async () => (await GetInstanceStateAsync(Parent, parentId)).Status == "F",
            "the child's fault must reach the parent", TimeSpan.FromSeconds(60));

        var data = await GetAttributesAsync(Parent, parentId);
        Assert.Equal("faulted", data.GetProperty("childOutcome").GetString());
        Assert.Equal(60, data.GetProperty("childAmount").GetInt32());
        Assert.Equal("wait", data.GetProperty("child").GetProperty("mode").GetString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<string> StartParentAsync(string customerId)
    {
        var parentId = await StartAsync(Parent, new
        {
            key = NewKey("dml-parent"),
            attributes = new { customer = new { id = customerId, name = "Lin" } }
        });
        await WaitUntilSettledAsync(Parent, parentId);
        return parentId;
    }

    /// <summary>Accepts a transition without waiting for the parent to settle (a SubFlow state parks it Busy).</summary>
    private async Task RunAsyncAccepted(string instanceId, string transitionKey, object body)
    {
        var status = await RunAsync(Parent, instanceId, transitionKey, body);
        Assert.True((int)status < 400, $"'{transitionKey}' was refused with {(int)status}");
    }

    private async Task<string> WaitForChildAsync(string parentId)
    {
        string? childId = null;
        await WaitUntilAsync(async () =>
        {
            var active = await GetActiveSubflowsAsync(Parent, parentId);
            childId = active.Values.FirstOrDefault();
            return childId is not null;
        }, "the parent must open a SubFlow correlation", TimeSpan.FromSeconds(60));
        return childId!;
    }

    private static async Task SkipUnlessMockLabAsync()
    {
        using var mocklab = new MockLabAdminClient();
        Skip.If(!await mocklab.IsUpAsync(),
            $"MockLab is not reachable at {mocklab.BaseUrl} — start it with `docker compose up -d` in the repo root.");
    }
}
