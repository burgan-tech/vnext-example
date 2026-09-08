using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// Rule ordering inside one boundary: <c>EffectivePriority</c> ascending, then specificity
/// descending, then declaration order.
/// </summary>
/// <remarks>
/// <para>
/// The subtle part is <c>EffectivePriority</c>. A rule that matches everything (no
/// <c>errorCodes</c>, no <c>errorTypes</c>) and is left at the default priority of 100 is demoted to
/// 999, so a specific rule at that same default 100 outranks it. Two catch-all rules at the default
/// would collide instead — the validator rejects duplicate effective priorities inside one boundary,
/// which is why the ordering case pairs a wildcard with a scoped rule rather than two wildcards.
/// </para>
/// <para>
/// Both cases below declare the LOSING rule first, so a runtime that simply took the first
/// declaration would produce the opposite verdict and fail these tests.
/// </para>
/// </remarks>
public class WithinLevelOrderingTests : ErrorBoundaryLabTestBase
{
    public WithinLevelOrderingTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task ASpecificRule_BeatsACatchAllLeftAtTheDefaultPriority()
    {
        // Declared: {abort, wildcard} then {rollback, errorCodes:["500"]}, both at the default
        // priority. The wildcard is demoted to 999, so rollback wins and the instance does not fault.
        var instanceId = await RunCaseAsync(Workflow, "case-within-level-order");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("rolled-back", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("Rollback", Text(incident, "boundaryAction"));
    }

    [Fact]
    public async Task TheLowerPriorityNumber_WinsRegardlessOfDeclarationOrder()
    {
        // Declared: {rollback, priority 200} then {notify, priority 50}. Both match the same 500.
        var instanceId = await RunCaseAsync(Workflow, "case-within-level-priority");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("notified", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("Notify", Text(incident, "boundaryAction"));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));
    }

    [Fact]
    public async Task ABoundaryThatRoutesToATransition_ResolvesItsOwnIncident()
    {
        // Rollback and notify both hand the pipeline a transition to run. When that transition
        // completes without faulting, FinalizeTransitionStep resolves the incident the boundary
        // opened — so a handled failure leaves history, not an open alarm.
        var instanceId = await RunCaseAsync(Workflow, "case-within-level-priority");

        var items = await GetIncidentItemsAsync(Workflow, instanceId);
        Assert.Single(items);
        Assert.True(Flag(items[0], "isResolved"),
            $"the boundary transition completed but its incident stayed open: {Summarize(items[0])}");
        Assert.NotEqual("-", Text(items[0], "resolvedAt"));

        var incident = await GetStateIncidentAsync(Workflow, instanceId);
        Assert.False(Flag(incident, "hasActiveIncident"));
        AssertAbsent(incident, "active", "the only incident is resolved");
    }
}
