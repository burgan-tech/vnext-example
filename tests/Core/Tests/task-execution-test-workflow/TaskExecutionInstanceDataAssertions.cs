using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.TaskExecutionTestWorkflow;

/// <summary>
/// Instance <c>attributes</c> sözleşmesi: <c>api-tests/task-execution/task-execution.http</c> B1/B3 yorumları ile
/// <c>core/Workflows/task-execution/src/.../*.csx</c> hizalıdır.
/// </summary>
internal static class TaskExecutionMainWorkflowInstanceDataAssertions
{
    private const string ContractHint =
        "(task-execution-test-workflow mappings + task-execution.http B1 expected flags)";

    /// <summary>
    /// Human-task beklemeden önce: HTTP, script, cross-workflow, start-flow, get-instance-data,
    /// notification, trigger, subprocess, get-instances ve human-task onEntry mapping bayrakları.
    /// </summary>
    public static void AssertWhileWaitingOnHumanTask(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "testId",
            $"testId {ContractHint}"
        );
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "httpTaskCompleted",
            ContractHint
        );
        JsonElementAssertions.AssertPropertyTrue(attributes, "scriptProcessed", ContractHint);
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "crossWorkflowCompleted",
            ContractHint
        );
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "startFlowCompleted",
            ContractHint
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "startedInstanceId",
            ContractHint
        );
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "getInstanceDataCompleted",
            ContractHint
        );

        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "notification" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "triggerTransition" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "subprocess" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "getInstances" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "humanTask" },
            "completed",
            ContractHint
        );
    }
}

/// <summary>
/// <c>extended-tasks-test-workflow</c> sonunda Dapr zinciri için <c>attributes</c> kontrolleri.
/// </summary>
internal static class ExtendedTasksWorkflowInstanceDataAssertions
{
    private const string ContractHint =
        "(extended-tasks-test-workflow Dapr mappings + task-execution.http B3 yorumları)";

    public static void AssertDaprChainCompleted(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(attributes, "initCompleted", ContractHint);
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprHttp" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprService" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprBinding" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprPubSub" },
            "completed",
            ContractHint
        );
    }
}
