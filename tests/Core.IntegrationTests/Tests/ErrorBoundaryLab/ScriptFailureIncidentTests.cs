using System.Net;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.ErrorBoundaryLab;

/// <summary>
/// Script failures and their incidents (vnext#1268), on <c>eb-scripts</c>: one case per place a
/// flow-author script can throw during a transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed and what must not.</b> An incident raised by a failing script used to carry an
/// empty <c>StackTrace</c>: the task executors, the transition mapper and the step catch kept only
/// the exception's message. The runtime now keeps the exception for the incident. The trace is
/// internal — the incident API never serves it — so these tests pin everything a client CAN see,
/// which the change must leave exactly as it was: the instance faults, one active incident, its
/// code, layer, task and boundary verdict. The stack trace itself is checked in the database, with
/// the query in the README.
/// </para>
/// <para>
/// <b>The rule case.</b> A failing automatic-transition rule counts as "not satisfied" by design:
/// the instance stays in the rule's state and nothing is recorded. The case pins that this change
/// did not turn it into an incident.
/// </para>
/// </remarks>
public class ScriptFailureIncidentTests : ErrorBoundaryLabTestBase
{
    private const string Scripts = "eb-scripts";
    private const string ScriptTask = "eb-script-fail-task";

    public ScriptFailureIncidentTests(VNextTestEnvironment environment) : base(environment) { }

    // Codes and messages as master (ee768e14) records them, measured before the change.
    [Theory]
    [InlineData("case-task-input", "Task:Script:eb-script-fail-task", "Task", ScriptTask, null,
        "Script input handler failed: eb-scripts: the input handler could not read the amount")]
    [InlineData("case-task-input-abort", "Task:400002", "Task", ScriptTask, "Abort",
        "Script input handler failed: eb-scripts: the input handler could not read the amount")]
    [InlineData("case-task-output", "Task:Script:eb-script-fail-task:500", "Task", ScriptTask, null,
        "Script output handler failed: eb-scripts: the output handler rejected the response")]
    [InlineData("case-task-output-nomatch", "Task:Script:eb-script-fail-task:500", "Task", ScriptTask, null,
        "Script output handler failed: eb-scripts: the output handler rejected the response")]
    [InlineData("case-transition-mapping", "Execution:200002", "Pipeline", null, null,
        "Failed to execute transition mapping: eb-scripts: the transition mapping found no customer")]
    [InlineData("case-lock-key", "Execution:200012", "Pipeline", null, null,
        "Failed to resolve resource lock key for transition 'case-lock-key': eb-scripts: the lock key script has no key")]
    [InlineData("case-timer", "Unexpected", "Pipeline", null, null,
        "eb-scripts: the timer script has no window")]
    public async Task ScriptFailure_FaultsWithOneIncident_WhoseVisibleFieldsAreUnchanged(
        string caseKey, string errorCode, string errorLayer, string? task, string? boundaryAction, string message)
    {
        var instanceId = await RunCaseAsync(Scripts, caseKey);
        await WaitUntilFaultedAsync(Scripts, instanceId);

        var (status, incident) = await GetActiveIncidentAsync(Scripts, instanceId);
        Assert.True(status == HttpStatusCode.OK, $"{caseKey}: no active incident ({(int)status})");

        var items = await GetIncidentItemsAsync(Scripts, instanceId);
        Assert.True(items.Count == 1,
            $"{caseKey}: expected one incident, got {items.Count}: {string.Join(" | ", items.Select(Summarize))}");

        Assert.Equal(errorCode, Text(incident, "errorCode"));
        Assert.Equal(errorLayer, Text(incident, "errorLayer"));
        Assert.Equal(task ?? "-", Text(incident, "task"));
        Assert.Equal(boundaryAction ?? "-", Text(incident, "boundaryAction"));
        Assert.Equal(message, Text(incident, "message"));
        AssertAbsent(incident, "stackTrace", "the incident API never serves the stack trace");
    }

    [Fact]
    public async Task FailingRule_IsNotSatisfied_AndRecordsNoIncident()
    {
        var instanceId = await StartCaseAsync(Scripts, "case-rule");
        var url = $"api/v1/core/workflows/{Scripts}/instances/{instanceId}/transitions/case-rule?sync=false";
        var (accepted, body) = await SendRawAsync(HttpMethod.Patch, url, new { }, Headers());
        Assert.True((int)accepted < 400, $"'case-rule' was refused with {(int)accepted}: {body}");

        // A state whose only way out is an automatic rule that never fires rests in Busy (the parked
        // auto-chain), so the settle signal is the state, not the status.
        await WaitUntilAsync(
            async () => (await GetInstanceStateAsync(Scripts, instanceId)).State == "rule-zone",
            $"{Scripts}/{instanceId} never reached rule-zone");

        var (_, status) = await GetInstanceStateAsync(Scripts, instanceId);
        Assert.NotEqual("F", status);

        var (incidentStatus, _) = await GetActiveIncidentAsync(Scripts, instanceId);
        Assert.Equal(HttpStatusCode.NotFound, incidentStatus);
        Assert.Empty(await GetIncidentItemsAsync(Scripts, instanceId));
    }
}
