using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.TaskExecutionTestWorkflow;

/*
!BUGS:
! 1. JsonElement from Dapr PubSub context.Body fails on JSON serialization:
!    InvalidOperationException due to disposed or invalid object state.
 * System.InvalidOperationException: Operation is not valid due to the current state of the object.
 *  at System.Text.Json.Serialization.Metadata.JsonPropertyInfo`1.GetMemberAndWriteJson(...)
 *  at BBT.Workflow.Tasks.Coordinator.TaskExecutionEngine.ExecuteCoreAsync(...):line 531
*/

/// <summary>
/// Integration tests aligned with <c>api-tests/task-execution/task-execution.http</c> B1–B3 and
/// the Task Execution section of <c>doc/integration-test-documentation.md</c>.
/// </summary>
public class TaskExecutionTestWorkflowTests : IntegrationTestBase
{
    private const string MainWorkflowKey = "task-execution-test-workflow";
    private const string TargetWorkflowKey = "task-target-workflow";
    private const string ExtendedWorkflowKey = "extended-tasks-test-workflow";

    /// <summary>Main chain: Mocklab HTTP, subprocess, external services — tolerant timeouts.</summary>
    private static readonly TimeSpan MainWorkflowStateTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ExtendedDaprTimeout = TimeSpan.FromMinutes(3);

    private readonly WorkflowInstanceTestHelper _mainWorkflow;
    private readonly WorkflowInstanceTestHelper _targetWorkflow;
    private readonly WorkflowInstanceTestHelper _extendedWorkflow;

    public TaskExecutionTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _mainWorkflow = new WorkflowInstanceTestHelper(Api, MainWorkflowKey);
        _targetWorkflow = new WorkflowInstanceTestHelper(Api, TargetWorkflowKey);
        _extendedWorkflow = new WorkflowInstanceTestHelper(Api, ExtendedWorkflowKey);
    }

    [Fact]
    public async Task B1_MainWorkflow_HappyPath_ReachesCompleted()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("task-exec-it");
        var instanceId = await _mainWorkflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "task-execution" },
                attributes = new { },
            }
        );

        // Flow: http -> script -> timer-wait (3s) -> start-flow -> get-instance-data
        // -> notification -> trigger-transition -> subprocess -> get-instances -> completed-state.
        // Human Task (type 5) is deprecated in runtime; not used — happy path completes without manual approval.
        await _mainWorkflow.WaitForStateAsync(
            instanceId,
            "completed-state",
            MainWorkflowStateTimeout
        );

        var stateCompleted = await _mainWorkflow.GetStateFunctionBodyAsync(
            instanceId,
            headers: null
        );
        var status = StateFunctionJson.ExtractStatus(stateCompleted);
        // On happy-path completion, GET .../functions/state must report status 'C' (Completed).
        Assert.Equal("C", status);

        var attrs = await GetAttributesAsync(MainWorkflowKey, instanceId);
        TaskExecutionMainWorkflowInstanceDataAssertions.AssertHappyPathCompleted(attrs);

        // Did Timer Task (type 9) actually wait? timer-wait-state scheduled transition + ITimerMapping
        // advances to start-flow-state after ~3s; auto-only would reach completed-state in ~0.5s.
        // timerStartedAt on parent attributes (TimerStartMapping) — elapsed time to completed-state should exceed 3s.
        // Use 2.5s tolerance; no upper bound (slow CI).
        var timerStartedAtRaw = attrs.GetProperty("timerStartedAt").GetString()!;
        var timerStartedAt = DateTime.Parse(
            timerStartedAtRaw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind
        );
        var timerElapsed = DateTime.UtcNow - timerStartedAt;
        Assert.True(
            timerElapsed.TotalSeconds >= 2.5,
            $"Timer Task (type 9) should delay scheduled transition by ~3s; "
                + $"elapsed since timerStartedAt={timerStartedAtRaw} is only {timerElapsed.TotalSeconds:F2}s. "
                + "If below 3s, the scheduled transition timer may be disabled."
        );

        // StartFlow + DirectTrigger: parent `startedInstanceId` alone is insufficient.
        // Confirm via target `functions/state`: (1) StartTask opened new instance, (2) DirectTriggerTask fired manual `manual-complete-target`.
        var startedTargetId = attrs.GetProperty("startedInstanceId").GetString();
        Assert.False(
            string.IsNullOrWhiteSpace(startedTargetId),
            "startedInstanceId should be a non-empty string from StartFlowMapping output. Because startflow task should start a workflow."
        );

        var targetStateAfterTrigger = await _targetWorkflow.GetStateFunctionBodyAsync(
            startedTargetId!,
            headers: null
        );
        Assert.Equal(
            "target-completed",
            StateFunctionJson.ExtractStateName(targetStateAfterTrigger)
        );
        Assert.Equal("C", StateFunctionJson.ExtractStatus(targetStateAfterTrigger));

        // SubProcessTask (type 14) starts a separate target instance (fire-and-forget).
        // attributes.subprocessInstanceId from SubProcessMapping OutputHandler; presence + target GET functions/state
        // Active/Completed proves startup (a "completed" flag alone is not enough). GetInstance checks
        // parentInstanceId/source/note on the child.
        var subprocessInstanceId = attrs.GetProperty("subprocessInstanceId").GetString();
        Assert.False(
            string.IsNullOrWhiteSpace(subprocessInstanceId),
            "subprocessInstanceId should be a non-empty string from SubProcessMapping output"
        );

        var subprocessStateBody = await _targetWorkflow.GetStateFunctionBodyAsync(
            subprocessInstanceId!,
            headers: null
        );
        // Fire-and-forget — do not drive transitions; remain target-initial or auto-completed.
        var subprocessState = StateFunctionJson.ExtractStateName(subprocessStateBody);
        var subprocessStatus = StateFunctionJson.ExtractStatus(subprocessStateBody);
        Assert.True(
            subprocessState == "target-initial" || subprocessState == "target-completed",
            $"subprocess instance should be in target-initial or target-completed, got '{subprocessState}'"
        );
        Assert.True(
            subprocessStatus == "A" || subprocessStatus == "C",
            $"subprocess instance status should be Active (A) or Completed (C), got '{subprocessStatus}'"
        );

        var subprocessAttrs = await GetAttributesAsync(TargetWorkflowKey, subprocessInstanceId!);
        JsonElementAssertions.AssertPropertyString(
            subprocessAttrs,
            "source",
            "task-execution-test",
            "subprocess instance attributes.source (set via SubProcessTask body)"
        );
        JsonElementAssertions.AssertPropertyString(
            subprocessAttrs,
            "parentInstanceId",
            instanceId,
            "subprocess instance attributes.parentInstanceId (must equal parent workflow instance id)"
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            subprocessAttrs,
            "note",
            "subprocess instance attributes.note (set via SubProcessTask body)"
        );

        // On SubProcess start, parent `functions/state` exposes `activeCorrelations`:
        //  - subFlowInstanceId matches parent attributes subprocessInstanceId,
        //  - subFlowType is SubProcess short code "P".
        // Proves runtime correlation, not only mapping-produced id (vnext-runtime function.md sub-flow table;
        // vnext-tests-as-code ActiveCorrelations section).
        // If parent is COMPLETED and activeCorrelations is empty this fails — move assertion to an earlier snapshot
        // taken while parent is still in subprocess-related state (error message mentions this).
        var correlationFound = StateFunctionJson.TryFindActiveCorrelationBySubFlowInstanceId(
            stateCompleted,
            subprocessInstanceId!,
            out var subprocessCorrelation
        );
        var allCorrelations = StateFunctionJson.ExtractActiveCorrelations(stateCompleted);
        Assert.True(
            correlationFound,
            $"parent functions/state.activeCorrelations must contain subFlowInstanceId == '{subprocessInstanceId}'; "
                + $"total correlations = {allCorrelations.Count}. "
                + "If zero, runtime may strip correlations once parent reaches COMPLETED; "
                + "then assert from a snapshot taken while parent is still in subprocess-state."
        );

        var subFlowType = StateFunctionJson.ExtractSubFlowType(subprocessCorrelation);
        Assert.True(
            string.Equals(subFlowType, "P", StringComparison.Ordinal),
            $"activeCorrelations[<subprocess>].subFlowType expected 'P' (SubProcess); actual = '{subFlowType ?? "<null>"}'."
        );
    }

    [Fact]
    public async Task B2_TargetWorkflow_HappyPath_ReachesTargetCompleted()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("task-target-it");
        var instanceId = await _targetWorkflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "task-execution", "target" },
                attributes = new { },
            }
        );

        await _targetWorkflow.WaitForStateAsync(
            instanceId,
            "target-initial",
            TimeSpan.FromSeconds(30)
        );

        await _targetWorkflow.RunTransitionAsync(
            instanceId,
            "manual-complete-target",
            headers: null,
            transitionBody: new { }
        );

        await _targetWorkflow.WaitForStateAsync(
            instanceId,
            "target-completed",
            TimeSpan.FromSeconds(30)
        );

        var targetStateBody = await _targetWorkflow.GetStateFunctionBodyAsync(
            instanceId,
            headers: null
        );
        var targetStatus = StateFunctionJson.ExtractStatus(targetStateBody);
        // Target workflow happy path: state function status must be 'C' (Completed).
        Assert.Equal("C", targetStatus);
    }

    [Fact]
    public async Task B3_ExtendedWorkflow_DaprChain_ReachesCompleted_WithExpectedTaskResults()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("extended-dapr-it");
        var instanceId = await _extendedWorkflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "task-execution", "dapr-only" },
                attributes = new { testId = "extended-tasks-dapr-path" },
            }
        );

        await _extendedWorkflow.WaitForStateAsync(
            instanceId,
            "completed-state",
            ExtendedDaprTimeout
        );

        var extendedStateBody = await _extendedWorkflow.GetStateFunctionBodyAsync(
            instanceId,
            headers: null
        );
        var extendedStatus = StateFunctionJson.ExtractStatus(extendedStateBody);
        // Dapr chain happy path: state function status must be 'C' (Completed).
        Assert.Equal("C", extendedStatus);

        var attrs = await GetAttributesAsync(ExtendedWorkflowKey, instanceId);
        JsonElementAssertions.AssertPropertyString(
            attrs,
            "testId",
            "extended-tasks-dapr-path",
            "start body testId should be preserved through init mapping"
        );
        ExtendedTasksWorkflowInstanceDataAssertions.AssertDaprChainCompleted(attrs);
    }

    private async Task<JsonElement> GetAttributesAsync(string workflowKey, string instanceId)
    {
        var response = await Api.GetInstanceAsync(workflowKey, instanceId);
        Assert.True(
            response.Body.TryGetProperty("attributes", out var attributes),
            "GetInstance response should include attributes"
        );
        return attributes;
    }
}
