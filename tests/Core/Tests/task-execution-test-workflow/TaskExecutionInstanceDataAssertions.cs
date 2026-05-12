using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.TaskExecutionTestWorkflow;

/// <summary>
/// Instance <c>attributes</c> contract aligned with <c>api-tests/task-execution/task-execution.http</c> B1/B3 comments and
/// <c>core/Workflows/task-execution/src/.../*.csx</c>.
/// </summary>
internal static class TaskExecutionMainWorkflowInstanceDataAssertions
{
    private const string ContractHint =
        "(task-execution-test-workflow mappings + task-execution.http B1 expected flags)";

    /// <summary>
    /// On happy-path completion: HTTP, script, timer-wait (Timer type 9), start-flow,
    /// get-instance-data, trigger, subprocess, get-instances onEntry mapping flags.
    /// Notification task uses <c>mapping.type: G</c> so <c>taskResults.notification</c> is not asserted.
    /// Human Task (type 5) is deprecated; not used — path reaches completed-state without human approval.
    /// timer-wait-state uses scheduled transition (<c>triggerType: 2</c>) + <c>ITimerMapping</c> ~3s wait;
    /// <c>timerStartedAt</c> is required and tests use it to verify real delay.
    /// </summary>
    public static void AssertHappyPathCompleted(JsonElement attributes)
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
        // SubProcessTask must start a new target instance; SubProcessMapping maps runtime id to parent subprocessInstanceId.
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
    }
}

/// <summary>
/// <c>attributes</c> checks for <c>extended-tasks-test-workflow</c> Dapr chain at completion.
/// Dapr Binding (type 2) state/task removed per integration-test-documentation "Untested Features";
/// chain is HTTP -&gt; Service -&gt; PubSub.
///
/// Per vnext-workflow-creation §6.4, each Dapr task asserts both literal <c>completed = true</c> and an
/// observable proof field:
///   - daprHttp / daprService: <c>processId</c> (GUID) from mocklab proves the call landed.
///   - daprPubSub: fire-and-forget returns no payload; runtime exposes <c>context.Body.isSuccess</c>; mapping writes
///     <c>published</c> on parent attributes; <c>published == true</c> proves successful publish.
/// </summary>
internal static class ExtendedTasksWorkflowInstanceDataAssertions
{
    private const string ContractHint =
        "(extended-tasks-test-workflow Dapr mappings + task-execution.http B3 comments)";

    public static void AssertDaprChainCompleted(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(attributes, "initCompleted", ContractHint);

        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprHttp" },
            "completed",
            ContractHint
        );
        // Skill §6.4: completed=true literal + mocklab GUID on parent proves real response handling.
        JsonElementAssertions.AssertNestedPropertyNonEmptyString(
            attributes,
            new[] { "taskResults", "daprHttp" },
            "processId",
            $"daprHttp.processId should come from mocklab response {ContractHint}"
        );

        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprService" },
            "completed",
            ContractHint
        );
        JsonElementAssertions.AssertNestedPropertyNonEmptyString(
            attributes,
            new[] { "taskResults", "daprService" },
            "processId",
            $"daprService.processId should come from mocklab response {ContractHint}"
        );

        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprPubSub" },
            "completed",
            ContractHint
        );
        // Fire-and-forget: runtime maps context.Body.isSuccess to parent "published".
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprPubSub" },
            "published",
            $"daprPubSub.published should reflect runtime isSuccess {ContractHint}"
        );
    }
}
