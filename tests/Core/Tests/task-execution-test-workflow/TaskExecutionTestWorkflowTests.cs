using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.TaskExecutionTestWorkflow;

/// <summary>
/// <c>api-tests/task-execution/task-execution.http</c> B1–B3 ve
/// <c>doc/integration-test-documentation.md</c> Task Execution grubu ile hizalı integration testler.
/// </summary>
public class TaskExecutionTestWorkflowTests : IntegrationTestBase
{
    private const string MainWorkflowKey = "task-execution-test-workflow";
    private const string TargetWorkflowKey = "task-target-workflow";
    private const string ExtendedWorkflowKey = "extended-tasks-test-workflow";

    /// <summary>Ana zincir: Mocklab HTTP, subprocess, harici servisler — toleranslı bekleme.</summary>
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
    public async Task B1_MainWorkflow_HappyPath_InstanceDataThenApprove_ReachesCompleted()
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

        await _mainWorkflow.WaitForStateAsync(
            instanceId,
            "human-task-state",
            MainWorkflowStateTimeout
        );

        var stateBody = await _mainWorkflow.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.True(
            StateFunctionJson.TransitionsContainKey(stateBody, "approve-human-task"),
            "human-task-state should expose approve-human-task transition"
        );

        var attrsBefore = await GetAttributesAsync(MainWorkflowKey, instanceId);
        TaskExecutionMainWorkflowInstanceDataAssertions.AssertWhileWaitingOnHumanTask(attrsBefore);

        // StartFlow + DirectTrigger zincirinin sadece parent instance'a `startedInstanceId` yazmış olması yeterli değildir.
        // Hedef workflow'un `functions/state` çağrısıyla; (1) StartTask yeni bir instance açtı, (2) DirectTriggerTask
        // hedefin manuel `manual-complete-target` geçişini gerçekten tetikledi, doğrulanır.
        var startedTargetId = attrsBefore.GetProperty("startedInstanceId").GetString();
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

        await _mainWorkflow.RunTransitionAsync(
            instanceId,
            "approve-human-task",
            headers: null,
            transitionBody: new { }
        );

        await _mainWorkflow.AssertStateAsync(instanceId, "completed-state");

        var stateCompleted = await _mainWorkflow.GetStateFunctionBodyAsync(
            instanceId,
            headers: null
        );
        var status = StateFunctionJson.ExtractStatus(stateCompleted);
        // Happy path bittiğinde instance tamamlanmıştır; GET .../functions/state gövdesindeki status 'C' (Completed) olmalıdır.
        Assert.Equal("C", status);

        var attrsAfter = await GetAttributesAsync(MainWorkflowKey, instanceId);
        TaskExecutionMainWorkflowInstanceDataAssertions.AssertWhileWaitingOnHumanTask(attrsAfter);
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
        // Hedef workflow happy path sonunda state fonksiyonu status 'C' (Completed) olmalıdır.
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
        // Dapr zinciri happy path sonunda state fonksiyonu status 'C' (Completed) olmalıdır.
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
