using System.Text.Json;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.TimeoutLab;

/// <summary>
/// The workflow-level timeout, end to end: published to the client as the state body's
/// <c>timeout</c> block while it is pending, and actually honoured when it fires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this scenario exists.</b> Two separate holes, one fixture. (1) The instant was already
/// persisted on the timeout <c>InstanceJob</c> row but no read surface exposed it, so a client could
/// not draw a countdown — the ask in vnext-client-sdk-core#59. (2) A SubFlow child's deadline may
/// come from its parent's <c>subFlow.overrides.timeout</c>, and that override was read only when the
/// job was armed: the job fired on schedule, the handler read the CHILD's own definition, found no
/// timeout and returned. The deadline was scheduled and unreachable.
/// </para>
/// <para>
/// <b>Why a new flow rather than an existing one.</b> Nothing in this repo exercised a workflow
/// timeout at all, and both authored durations are <c>PT15M</c> — unwatchable inside a test run.
/// <c>subflow-orchestration</c>'s override must STAY at <c>PT15M</c>: now that the runtime honours
/// it, shortening it would start cancelling that scenario's children mid-suite.
/// </para>
/// <para>
/// <b>What is deliberately NOT asserted.</b> That the deadline resets on activity — it does not.
/// <c>timer.reset</c> is required by the schema and read nowhere in the runtime, so the duration is
/// an absolute budget from instance start. See
/// <c>vnext-meta/known-issues.json → workflow-timeout-reset-not-implemented</c>.
/// </para>
/// </remarks>
public class TimeoutLabTests : WorkflowTestBase
{
    private const string RootWorkflow = "timeout-lab-root";
    private const string ParentWorkflow = "timeout-lab-parent";

    private const string RootWaitingState = "root-waiting";
    private const string RootTimedOutState = "root-timedout";
    private const string ChildTimedOutState = "child-timedout";

    /// <summary>The duration both fixtures declare; the waits below allow generous slack over it.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public TimeoutLabTests(VNextTestEnvironment environment) : base(environment)
    {
    }

    /// <summary>
    /// While the deadline is pending, the state body carries it: the declared key, the state the
    /// instance will be pulled to, and a UTC instant in the future. And when it fires, the instance
    /// really lands on that target — the same <c>target</c> the client was shown, which is what the
    /// shared effective-timeout resolver guarantees.
    /// </summary>
    [Fact]
    public async Task RootFlow_PublishesItsDeadline_AndIsPulledToTheTargetItPublished()
    {
        var instanceId = await StartAsync(RootWorkflow, new { });

        await WaitForInstanceStateAsync(RootWorkflow, instanceId, RootWaitingState);

        // ── published while pending ────────────────────────────────────────────
        var timeout = await GetTimeoutBlockAsync(RootWorkflow, instanceId);

        Assert.True(timeout.HasValue,
            $"no `timeout` block on a parked instance that declares one — " +
            $"{await DescribeAsync(RootWorkflow, instanceId)}");

        Assert.Equal("root-abandoned", timeout!.Value.GetProperty("key").GetString());
        Assert.Equal(RootTimedOutState, timeout.Value.GetProperty("target").GetString());

        var executeAtRaw = timeout.Value.GetProperty("executeAtUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(executeAtRaw), "executeAtUtc was empty");
        Assert.EndsWith("Z", executeAtRaw, StringComparison.Ordinal);

        var executeAt = DateTimeOffset.Parse(executeAtRaw!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(executeAt > DateTimeOffset.UtcNow,
            $"the published deadline {executeAt:O} is already in the past on a freshly started instance");

        // ── honoured when it fires ─────────────────────────────────────────────
        await WaitUntilAsync(
            async () =>
            {
                var (state, status) = await GetInstanceStateAsync(RootWorkflow, instanceId);
                return state == RootTimedOutState && TerminalStatuses.Contains(status);
            },
            $"the timeout never pulled the instance to '{RootTimedOutState}' — " +
            $"{await DescribeAsync(RootWorkflow, instanceId)}",
            Deadline + TimeSpan.FromSeconds(40));

        // ── and stops being published once it can no longer fire ───────────────
        var afterwards = await GetTimeoutBlockAsync(RootWorkflow, instanceId);
        Assert.False(afterwards.HasValue,
            "the `timeout` block is still served on a terminal instance; the read-side guard is " +
            "supposed to suppress it without waiting for the asynchronous cancel-cleanup chain to " +
            "close the job row");
    }

    /// <summary>
    /// The regression. The child declares <c>"timeout": null</c> and runs entirely under the
    /// parent's <c>subFlow.overrides.timeout</c>. It must BOTH report that override's key/target on
    /// its own state read AND actually be pulled to it.
    /// </summary>
    /// <remarks>
    /// Before the effective-timeout resolver, the first assertion had nothing to read (the child's
    /// own definition carries no timeout) and the second never happened at all: the job fired and
    /// the handler returned on <c>TimeoutConfigMissing</c>.
    /// </remarks>
    [Fact]
    public async Task SubFlowChild_ReportsAndHonoursTheParentsTimeoutOverride()
    {
        var parentId = await StartAsync(ParentWorkflow, new { });

        string childId = null!;
        await WaitUntilAsync(
            async () =>
            {
                var subflows = await GetActiveSubflowsAsync(ParentWorkflow, parentId);
                if (!subflows.TryGetValue("timeout-lab-child", out var id)) return false;
                childId = id;
                return true;
            },
            $"the parent never opened its child correlation — {await DescribeAsync(ParentWorkflow, parentId)}");

        // ── the child publishes the PARENT's deadline, not its own (it has none) ──
        var timeout = await GetTimeoutBlockAsync("timeout-lab-child", childId);

        Assert.True(timeout.HasValue,
            "the child serves no `timeout` block, even though its parent supplied one through " +
            "subFlow.overrides.timeout — the read is resolving the child's own definition instead " +
            "of the effective timeout");

        Assert.Equal("child-abandoned", timeout!.Value.GetProperty("key").GetString());
        Assert.Equal(ChildTimedOutState, timeout.Value.GetProperty("target").GetString());

        // The override's annotations travel with it — the stamp the parent writes carries them,
        // and they replace the child's (the child declares no timeout, so it has none of its own).
        Assert.Equal("parent-override", Annotation(timeout.Value, "ui/countdown"));

        // ── and the runtime moves it to exactly that target ──────────────────────
        await WaitUntilAsync(
            async () =>
            {
                var (state, status) = await GetInstanceStateAsync("timeout-lab-child", childId);
                return state == ChildTimedOutState && TerminalStatuses.Contains(status);
            },
            $"the parent's timeout override never fired on the child — it was armed (the block above " +
            $"proves the instant exists) but the instance stayed put. " +
            $"{await DescribeAsync("timeout-lab-child", childId)}",
            Deadline + TimeSpan.FromSeconds(40));
    }

    /// <summary>
    /// Every entry the state body lists carries its definition's <c>annotations</c>: the state,
    /// shared and the three well-known workflow-level transitions, and the <c>timeout</c> block. Each
    /// fixture entry carries a distinct <c>ui/source</c> value, so a dropped or crossed annotation
    /// names itself. (Scheduled entries are pinned by <c>schedule-after-auto</c>.)
    /// </summary>
    [Fact]
    public async Task RootFlow_StateBodyCarriesTheAnnotationsOfEveryListedEntry()
    {
        var instanceId = await StartAsync(RootWorkflow, new { });

        await WaitForInstanceStateAsync(RootWorkflow, instanceId, RootWaitingState);

        var response = await Api.CallInstanceFunctionAsync(RootWorkflow, instanceId, "state", headers: Headers());
        var body = response.Body;

        var bySource = new Dictionary<string, (string Kind, string? Source)>(StringComparer.Ordinal);
        foreach (var transition in body.GetProperty("transitions").EnumerateArray())
        {
            var name = transition.GetProperty("name").GetString()!;
            bySource[name] = (transition.GetProperty("kind").GetString()!, Annotation(transition, "ui/source"));
        }

        var described = string.Join(", ", bySource.Select(kv => $"{kv.Key}={kv.Value.Kind}/{kv.Value.Source ?? "∅"}"));
        Assert.Equal(("stateTransition", "state"), bySource.GetValueOrDefault("root-finish"));
        Assert.Equal(("sharedTransition", "shared"), bySource.GetValueOrDefault("root-note"));
        Assert.Equal(("cancel", "cancel"), bySource.GetValueOrDefault("cancel-timeout-lab-root"));
        Assert.Equal(("exit", "exit"), bySource.GetValueOrDefault("exit-timeout-lab-root"));
        Assert.True(bySource.GetValueOrDefault("update-timeout-lab-root") == ("updateData", "updateData"),
            $"updateData entry missing or without its annotation — transitions: {described}");

        Assert.True(body.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.Object,
            $"no `timeout` block — {await DescribeAsync(RootWorkflow, instanceId)}");
        Assert.Equal("root-deadline", Annotation(timeout, "ui/countdown"));
    }

    private static string? Annotation(JsonElement element, string key) =>
        element.TryGetProperty("annotations", out var annotations)
        && annotations.ValueKind == JsonValueKind.Object
        && annotations.TryGetProperty(key, out var value)
            ? value.GetString()
            : null;

    /// <summary>
    /// The <c>timeout</c> block describes the polled instance and nothing else: a parent whose child
    /// carries a deadline must not inherit it, and must not be given one it does not have.
    /// </summary>
    [Fact]
    public async Task ParentDoesNotInheritItsChildsDeadline()
    {
        var parentId = await StartAsync(ParentWorkflow, new { });

        await WaitUntilAsync(
            async () => (await GetActiveSubflowsAsync(ParentWorkflow, parentId)).ContainsKey("timeout-lab-child"),
            $"the parent never opened its child correlation — {await DescribeAsync(ParentWorkflow, parentId)}");

        var parentTimeout = await GetTimeoutBlockAsync(ParentWorkflow, parentId);

        Assert.False(parentTimeout.HasValue,
            "the parent is serving a `timeout` block; the parent declares no timeout of its own, so " +
            "this can only have been lifted from the active subflow — the block is defined to " +
            "describe the polled instance only");
    }

    /// <summary>
    /// Reads the state body's <c>timeout</c> block, or null when the property is absent — which is
    /// how "no deadline" is expressed on the wire (the property is omitted, never emitted as null).
    /// </summary>
    private async Task<JsonElement?> GetTimeoutBlockAsync(string workflow, string instanceId)
    {
        var response = await Api.CallInstanceFunctionAsync(workflow, instanceId, "state", headers: Headers());
        return response.Body.TryGetProperty("timeout", out var timeout)
               && timeout.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? timeout
            : null;
    }
}
