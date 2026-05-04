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
    /// Happy path tamamlandiginda: HTTP, script, timer-wait (Timer Task tip 9), start-flow,
    /// get-instance-data, trigger, subprocess ve get-instances onEntry mapping bayraklari.
    /// Notification task <c>mapping.type: G</c> kullandigi icin <c>taskResults.notification</c>
    /// entegrasyon assert'ine dahil edilmez. Human Task (tip 5) runtime tarafindan kaldirilacak
    /// gecici bir ozellik oldugu icin bu workflow'da kullanilmaz; happy path human onayi olmadan
    /// dogrudan completed-state'e ulasir. timer-wait-state, scheduled transition
    /// (<c>triggerType: 2</c>) + <c>ITimerMapping</c> ile 3 sn bekledigi icin
    /// <c>timerStartedAt</c> attribute'u zorunludur ve test bunun uzerinden gercek bekleme
    /// suresini dogrular.
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
    }
}

/// <summary>
/// <c>extended-tasks-test-workflow</c> sonunda Dapr zinciri için <c>attributes</c> kontrolleri.
/// Dapr Binding (tip 2) state ve task'i workflow'dan kaldirildi (bkz. integration-test-documentation.md
/// "Test Edilmeyen Ozellikler" bolumu); zincir su an HTTP -> Service -> PubSub seklinde ilerler.
///
/// Skill vnext-workflow-creation §6.4 geregi her Dapr task'i icin sabit "completed = true" literal
/// bayraginin yaninda, task'in gercekten calistigini kanitlayan ek bir alan da assert edilir:
///   - daprHttp / daprService: mocklab yanitindan gelen <c>processId</c> (GUID) parent attributes'a
///     yazilir; non-empty string olmasi mocklab'a gercekten ulasildiginin kanitidir.
///   - daprPubSub: PubSub fire-and-forget oldugundan response body'sinde alan donmez; runtime
///     <c>context.Body.isSuccess</c> bayragini doner ve mapping bunu <c>published</c> olarak
///     parent attributes'a yazar; <c>published == true</c> task'in basarili publish ettiginin kanitidir.
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
        // Skill §6.4: mapping bayragi "completed=true" literal; gercek task yanitinin (mocklab GUID)
        // parent attributes'a yansidigi processId ile kanitlanir.
        JsonElementAssertions.AssertNestedPropertyNonEmptyString(
            attributes,
            new[] { "taskResults", "daprHttp" },
            "processId",
            $"daprHttp.processId mocklab yanitindan gelmeli {ContractHint}"
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
            $"daprService.processId mocklab yanitindan gelmeli {ContractHint}"
        );

        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprPubSub" },
            "completed",
            ContractHint
        );
        // PubSub fire-and-forget: runtime context.Body.isSuccess'i mapping parent attributes'a
        // "published" olarak yazar; true olmasi PubSub broker'a gercekten gonderildigin kanitidir.
        JsonElementAssertions.AssertNestedPropertyTrue(
            attributes,
            new[] { "taskResults", "daprPubSub" },
            "published",
            $"daprPubSub.published runtime isSuccess bayragindan gelmeli {ContractHint}"
        );
    }
}
