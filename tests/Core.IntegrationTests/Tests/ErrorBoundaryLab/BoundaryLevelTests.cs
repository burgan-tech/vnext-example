using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// Which boundary handles a failure, and what each level's verdict looks like on the incident.
/// </summary>
/// <remarks>
/// <para>
/// Boundaries can be declared in three places — on a task reference inside a hook, on a state, and
/// on the workflow — and the runtime resolves them Task → State → Global, taking the FIRST level
/// that yields any match. Level dominance is decided before priority is read, which is the part
/// that surprises people: a task-level wildcard at priority 999 beats a state-level rule at
/// priority 1. Every case here lands its verdict in <c>boundaryLevel</c> on the incident, so the
/// assertion is about the resolved level itself rather than a side effect of it.
/// </para>
/// <para>
/// The state-level cases route through a "zone" state whose <c>onEntries</c> carry the failing task.
/// That is not decoration: the state boundary the runtime consults is the one on
/// <c>instance.CurrentState</c>, and OnExecute (order 30) runs before ChangeState (50) while OnEntry
/// (60) runs after it. A failing task on a transition therefore sees the SOURCE state's boundary,
/// and only a task on the target's onEntry sees the target's.
/// </para>
/// </remarks>
public class BoundaryLevelTests : ErrorBoundaryLabTestBase
{
    public BoundaryLevelTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task ATaskLevelBoundary_ResolvesAtTaskLevel()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-task-rollback");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("rolled-back", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("Rollback", Text(incident, "boundaryAction"));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));
        Assert.Equal("Task:Http:eb-http-500-task-b:500", Text(incident, "errorCode"));
        Assert.Equal(500, Number(incident, "statusCode"));
    }

    [Fact]
    public async Task AStateLevelBoundary_ResolvesAtStateLevel()
    {
        var instanceId = await RunCaseAsync(Workflow, "case-state-notify");

        var (state, status) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("notified", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("Notify", Text(incident, "boundaryAction"));
        Assert.Equal("State", Text(incident, "boundaryLevel"));
        // The incident is attributed to the zone, because that is where the instance was when its
        // onEntry task failed.
        Assert.Equal("zone-state-notify", Text(incident, "state"));
    }

    [Fact]
    public async Task AGlobalBoundary_ResolvesAtGlobalLevel()
    {
        var instanceId = await RunCaseAsync(GlobalWorkflow, "g-case-global-rollback");

        var (state, status) = await GetInstanceStateAsync(GlobalWorkflow, instanceId);
        Assert.Equal("rolled-back", state);
        Assert.NotEqual("F", status);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(GlobalWorkflow, instanceId));
        Assert.Equal("Rollback", Text(incident, "boundaryAction"));
        Assert.Equal("Global", Text(incident, "boundaryLevel"));
        Assert.Equal(503, Number(incident, "statusCode"));
    }

    [Fact]
    public async Task ATaskBoundary_WinsOverAMoreSpecificStateBoundary()
    {
        // The zone's rule is scoped to "500" (specific) at priority 1 (highest); the task's is a
        // bare wildcard at priority 999 (lowest). The task rule still wins — level beats priority.
        var instanceId = await RunCaseAsync(Workflow, "case-precedence-task-over-state");
        await WaitUntilFaultedAsync(Workflow, instanceId);

        var (state, _) = await GetInstanceStateAsync(Workflow, instanceId);
        Assert.Equal("zone-precedence", state);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(Workflow, instanceId));
        Assert.Equal("Task", Text(incident, "boundaryLevel"));
        Assert.Equal("Abort", Text(incident, "boundaryAction"));
    }

    [Fact]
    public async Task AStateBoundary_WinsOverTheGlobalOne()
    {
        // Both rules match the same 503; the state's notify shadows the workflow's rollback.
        var instanceId = await RunCaseAsync(GlobalWorkflow, "g-case-state-over-global");

        var (state, _) = await GetInstanceStateAsync(GlobalWorkflow, instanceId);
        Assert.Equal("notified", state);

        var incident = BoundaryIncident(await GetIncidentItemsAsync(GlobalWorkflow, instanceId));
        Assert.Equal("State", Text(incident, "boundaryLevel"));
        Assert.Equal("Notify", Text(incident, "boundaryAction"));
    }

    [Fact]
    public async Task WhenBoundariesExistButNoneMatch_TheInstanceFaultsWithAnUnattributedIncident()
    {
        // The global rule is scoped to 503; this case fails with 500. The failure is unhandled, so
        // the instance faults and the incident carries no boundary verdict — the shape that says
        // "nothing decided this", as opposed to "a boundary decided to abort".
        var instanceId = await RunCaseAsync(GlobalWorkflow, "g-case-unhandled-no-match");
        await WaitUntilFaultedAsync(GlobalWorkflow, instanceId);

        // One unhandled failure, one row: the task step records the incident and commits it with the
        // HasActiveIncident flag, so the fault path adds no second, unattributed row of its own.
        var items = await GetIncidentItemsAsync(GlobalWorkflow, instanceId);
        var incident = Assert.Single(items);

        AssertAbsent(incident, "boundaryAction", "no rule matched, so no action was taken");
        AssertAbsent(incident, "boundaryLevel", "no rule matched, so no level resolved");

        // It is still attributed to the task that failed — that is what distinguishes an unhandled
        // task failure from the pipeline-layer fallback row.
        Assert.Equal("eb-http-500-task-i", Text(incident, "task"));
        Assert.Equal("Task:Http:eb-http-500-task-i:500", Text(incident, "errorCode"));
        Assert.Equal(500, Number(incident, "statusCode"));
    }
}
