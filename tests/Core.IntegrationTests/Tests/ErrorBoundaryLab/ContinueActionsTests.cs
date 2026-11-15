using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// The actions that let the pipeline carry on after a task failed — <c>ignore</c> (3) and
/// <c>log</c> (5) — and the case with no boundary declared anywhere, which does NOT carry on.
/// </summary>
/// <remarks>
/// <para>
/// Each case pairs the failing task (order 1) with a marker task (order 2) that stamps
/// <c>markerAfterFailure</c> into instance data, so whether the rest of the hook ran is read from
/// the data, not inferred.
/// </para>
/// <para>
/// <b>ignore / log</b> (vnext#1257): the transition finishes and the hook's remaining tasks run. No
/// incident is recorded — the documented informational, already-resolved incident is still not
/// written, because the engine reports a continue-style outcome with no boundary action attached.
/// Up to v0.0.106 the coordinator stopped the hook at the failed task, so the marker never ran.
/// </para>
/// <para>
/// <b>No boundary at any level</b> (vnext#1257): the failure is unhandled. The hook stops at the
/// failed task, the task step records one incident attributed to that task with no boundary verdict,
/// and the instance faults. Up to v0.0.106 the transition completed into <c>landed</c> with no
/// incident and the rest of the hook was silently skipped — the defect reported in
/// vnext-client-sdk-core#95.
/// </para>
/// </remarks>
public class ContinueActionsTests : ErrorBoundaryLabTestBase
{
    public ContinueActionsTests(VNextTestEnvironment environment) : base(environment) { }

    [Theory]
    [InlineData("case-ignore", "ignore (3)")]
    [InlineData("case-log", "log (5)")]
    public async Task AContinueStyleOutcome_LetsTheTransitionFinishWithoutAnIncident(
        string caseKey, string description)
    {
        var instanceId = await RunCaseAsync(Workflow, caseKey);

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("landed", state);
        Assert.NotEqual("F", status);

        Assert.Empty(await GetIncidentItemsAsync(Workflow, instanceId));

        // The block is always emitted (flag plus links), so "no incident" shows as an absent
        // `active` link rather than an absent block. Asserting the block away would only prove the
        // runtime stopped emitting it.
        var metadata = await GetIncidentMetadataAsync(Workflow, instanceId);
        Assert.NotNull(metadata);
        Assert.False(Flag(metadata!.Value, "hasActiveIncident"));
        AssertAbsent(metadata.Value, "active", $"{description} recorded no incident");

        var incident = await GetStateIncidentAsync(Workflow, instanceId);
        Assert.False(Flag(incident, "hasActiveIncident"),
            $"{description} left the state block reporting an active incident");
        AssertAbsent(incident, "active", $"{description} recorded no incident");
    }

    [Theory]
    [InlineData("case-ignore", "ignore (3)")]
    [InlineData("case-log", "log (5)")]
    public async Task AContinueStyleOutcome_RunsTheRestOfItsHook(string caseKey, string description)
    {
        var instanceId = await RunCaseAsync(Workflow, caseKey);

        var attributes = await GetAttributesAsync(Workflow, instanceId);

        Assert.True(attributes.TryGetProperty("markerAfterFailure", out _),
            $"after {description} the hook's remaining tasks did not run");

        // The failing task itself did run, so the case is not vacuously passing.
        Assert.True(attributes.TryGetProperty("httpAttempts", out var attempts) && attempts.GetInt32() >= 1,
            "the failing task never ran — this case proves nothing");
    }

    [Fact]
    public async Task WithNoBoundaryAnywhere_TheFailureFaultsTheInstanceWithAnIncident()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-unhandled");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        // The transition did not complete: the instance stays on the hub it started from.
        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("F", status);
        Assert.NotEqual("landed", state);

        // One unhandled failure, one row, attributed to the task that failed and carrying no
        // boundary verdict — nothing declared, so nothing decided.
        var incident = Assert.Single(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("eb-http-500-task-h", Text(incident, "task"));
        Assert.Equal("Task:Http:eb-http-500-task-h:500", Text(incident, "errorCode"));
        Assert.Equal(500, Number(incident, "statusCode"));
        AssertAbsent(incident, "boundaryAction", "no boundary is declared, so no action was taken");
        AssertAbsent(incident, "boundaryLevel", "no boundary is declared, so no level resolved");

        var (activeStatus, _) = await GetActiveIncidentAsync(Workflow, instanceId);
        Assert.Equal(System.Net.HttpStatusCode.OK, activeStatus);

        // The hook stopped at the failed task: the marker after it never ran.
        var attributes = await GetAttributesAsync(Workflow, instanceId);
        Assert.False(attributes.TryGetProperty("markerAfterFailure", out _),
            "the hook ran on past an unhandled failure");
    }
}
