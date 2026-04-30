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
    /// Human-task beklemeden önce: HTTP, script, timer-wait (Timer Task tip 9), start-flow,
    /// get-instance-data, trigger, subprocess, get-instances ve human-task onEntry mapping
    /// bayrakları. Notification task <c>mapping.type: G</c> kullandığı için
    /// <c>taskResults.notification</c> entegrasyon assert'ine dahil edilmez.
    /// timer-wait-state, scheduled transition (<c>triggerType: 2</c>) + <c>ITimerMapping</c>
    /// ile 3 sn beklediği için <c>timerStartedAt</c> attribute'ü zorunludur ve test bunun
    /// üzerinden gerçek bekleme süresini doğrular.
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

        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "timerStartedAt",
            $"timerStartedAt {ContractHint}"
        );
        Assert.True(
            attributes.TryGetProperty("timerExpectedSeconds", out var expectedSeconds)
                && expectedSeconds.ValueKind == JsonValueKind.Number,
            $"timerExpectedSeconds should be a JSON number {ContractHint}"
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
        // SubProcessTask (tip 14) gercekten yeni bir hedef instance acmali; SubProcessMapping
        // OutputHandler'i runtime yanitindaki id alanini parent attributes.subprocessInstanceId'ye yazar.
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "subprocessInstanceId",
            ContractHint
        );
        Assert.True(
            attributes.TryGetProperty("subprocessData", out var subprocessData)
                && subprocessData.ValueKind == JsonValueKind.Object,
            $"subprocessData should be a JSON object (SubProcessTask response data snapshot) {ContractHint}"
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
