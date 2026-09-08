using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// The actions that let the pipeline carry on after a task failed — <c>ignore</c> (3) and
/// <c>log</c> (5) — and the control case with no boundary declared anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything in this class is MEASURED, not designed.</b> The documented intent for
/// ignore/log is an informational, already-resolved incident; the runtime records none, because the
/// engine reports a continue-style outcome as a result with no boundary action attached and the
/// pipeline step falls through to "continue" without ever reaching the incident-recording branch.
/// A second measured consequence: the coordinator stops the hook at the failed task, so the tasks
/// declared AFTER it never run. Each case therefore pairs the failing task with a marker task that
/// stamps instance data, and the absence of that stamp is the evidence.
/// </para>
/// <para>
/// If a future runtime records the informational incident or finishes the hook, these tests go red —
/// deliberately. They are the record of what the engine does today, and the trigger to update the
/// documentation when it changes.
/// </para>
/// <para>
/// The third case is the control that separates "no boundary" from "boundary that did not match":
/// with nothing declared at any level the failure is not acted on at all — no fault, no incident —
/// while a declared-but-unmatched boundary faults the instance (see
/// <see cref="BoundaryLevelTests.WhenBoundariesExistButNoneMatch_TheInstanceFaultsWithAnUnattributedIncident"/>).
/// Absence of a boundary means "the failure is not acted on", not "the failure faults the instance".
/// </para>
/// </remarks>
public class ContinueActionsTests : ErrorBoundaryLabTestBase
{
    public ContinueActionsTests(VNextTestEnvironment environment) : base(environment) { }

    [Theory]
    [InlineData("case-ignore", "ignore (3)")]
    [InlineData("case-log", "log (5)")]
    [InlineData("case-unhandled", "no boundary at any level")]
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
    [InlineData("case-unhandled", "no boundary at any level")]
    public async Task AFailedTask_StopsTheRestOfItsHookFromRunning(string caseKey, string description)
    {
        var instanceId = await RunCaseAsync(Workflow, caseKey);

        var attributes = await GetAttributesAsync(Workflow, instanceId);

        Assert.False(attributes.TryGetProperty("markerAfterFailure", out _),
            $"MEASURED behaviour changed: after {description} the hook's remaining tasks now run. " +
            "That is arguably the better semantics — update this test, the README and the " +
            "TEST-SCENARIOS gap list together.");

        // The failing task itself did run, so the case is not vacuously passing.
        Assert.True(attributes.TryGetProperty("httpAttempts", out var attempts) && attempts.GetInt32() >= 1,
            "the failing task never ran — this case proves nothing");
    }
}
