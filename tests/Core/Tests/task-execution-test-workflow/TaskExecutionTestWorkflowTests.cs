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

        // Workflow zinciri: http -> script -> timer-wait (3 sn) -> start-flow -> get-instance-data
        // -> notification -> trigger-transition -> subprocess -> get-instances -> completed-state.
        // Human Task (tip 5) runtime tarafindan kaldirilacak gecici bir ozellik oldugu icin bu
        // workflow'da kullanilmaz; happy path manuel onay olmadan dogrudan completed-state'e ulasir.
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
        // Happy path bittiginde instance tamamlanmistir; GET .../functions/state govdesindeki status 'C' (Completed) olmalidir.
        Assert.Equal("C", status);

        var attrs = await GetAttributesAsync(MainWorkflowKey, instanceId);
        TaskExecutionMainWorkflowInstanceDataAssertions.AssertHappyPathCompleted(attrs);

        // Timer Task (tip 9) gercekten beklemis mi? timer-wait-state'in scheduled transition'i
        // ITimerMapping ile 3 sn sonra start-flow-state'e gecirir; eger sadece auto transition
        // calissaydi completed-state'e ~yari saniyede ulasirdik. timerStartedAt parent attributes'unda
        // yazildigi icin (TimerStartMapping) completed-state'e ulasilan ana kadar gecen sure 3 sn'den
        // buyuk olmalidir. Toleransi 2.5 sn olarak aliyoruz; ust sinir koymuyoruz (CI yavasligi).
        var timerStartedAtRaw = attrs.GetProperty("timerStartedAt").GetString()!;
        var timerStartedAt = DateTime.Parse(
            timerStartedAtRaw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind
        );
        var timerElapsed = DateTime.UtcNow - timerStartedAt;
        Assert.True(
            timerElapsed.TotalSeconds >= 2.5,
            $"Timer Task (tip 9) should have delayed scheduled transition by ~3s; "
                + $"elapsed since timerStartedAt={timerStartedAtRaw} is only {timerElapsed.TotalSeconds:F2}s. "
                + "Eger bu deger 3 sn'den kucukse scheduled transition timer'i devre disi kalmis olabilir."
        );

        // StartFlow + DirectTrigger zincirinin sadece parent instance'a `startedInstanceId` yazmis olmasi yeterli degildir.
        // Hedef workflow'un `functions/state` cagrisiyla; (1) StartTask yeni bir instance acti, (2) DirectTriggerTask
        // hedefin manuel `manual-complete-target` gecisini gercekten tetikledi, dogrulanir.
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

        // SubProcessTask (tip 14) ayri bir hedef instance acar (fire-and-forget).
        // attributes.subprocessInstanceId, SubProcessMapping OutputHandler'inda runtime yanitindan yazilir;
        // varligi ve hedef workflow GET functions/state cevabinin Active/Completed olmasi, subprocess'in
        // gercekten baslatildigini kanitlar (sadece "completed" bayragi yetmez). Ayrica GetInstance ile
        // hedef instance attributes'inda parentInstanceId/source/note alanlarinin yazildigini dogrulariz.
        var subprocessInstanceId = attrs.GetProperty("subprocessInstanceId").GetString();
        Assert.False(
            string.IsNullOrWhiteSpace(subprocessInstanceId),
            "subprocessInstanceId should be a non-empty string from SubProcessMapping output"
        );

        var subprocessStateBody = await _targetWorkflow.GetStateFunctionBodyAsync(
            subprocessInstanceId!,
            headers: null
        );
        // Subprocess fire-and-forget oldugu icin tetiklemiyoruz; ya target-initial'da Active kalmali
        // ya da runtime tarafindan otomatik Completed olmus olmali. Her iki durumda da instance'in
        // gercekten yaratildigi GET 200 + gecerli state ile teyit edilir.
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

        // SubProcess (tip 14) baslatildiginda parent instance'in `functions/state` cevabindaki
        // `activeCorrelations` listesinde subprocess'e karsilik gelen bir correlation bulunur:
        //  - `subFlowInstanceId` parent attributes'taki `subprocessInstanceId` ile birebir esit,
        //  - `subFlowType` SubProcess kisa kodu olan "P".
        // Bu, parent'in attributes'ina yazilan id'nin (mapping uretimi) yaninda runtime'in da
        // sub-flow correlation'ini gercekten kaydettigini kanitlar; sadece "completed = true"
        // bayragi yetmez (vnext-runtime/doc/tr/flow/function.md "Sub-flow Korelasyonlari" tablosu;
        // vnext-tests-as-code skill "ActiveCorrelations ile SubProcess / SubFlow teyidi" bolumu).
        // Not: Eger parent COMPLETED iken activeCorrelations bos donerse bu adim fail olur ve
        // doğrulamayi parent subprocess'i baslattiktan hemen sonraki bir state snapshot'inda
        // yapmaya tasimak gerekir; ilk fail durumunda hata mesaji bu durumu da belirtir.
        var correlationFound = StateFunctionJson.TryFindActiveCorrelationBySubFlowInstanceId(
            stateCompleted,
            subprocessInstanceId!,
            out var subprocessCorrelation
        );
        var allCorrelations = StateFunctionJson.ExtractActiveCorrelations(stateCompleted);
        Assert.True(
            correlationFound,
            $"parent functions/state.activeCorrelations icinde subFlowInstanceId == '{subprocessInstanceId}' olan correlation bulunmali; "
                + $"toplam correlation sayisi = {allCorrelations.Count}. "
                + "Eger 0 geldiyse parent COMPLETED'a ulastiginda runtime correlation'i listeden cikarmis olabilir; "
                + "doğrulamayi parent subprocess-state'inde iken alinan bir snapshot'a tasimak gerekebilir."
        );

        var subFlowType = StateFunctionJson.ExtractSubFlowType(subprocessCorrelation);
        Assert.True(
            string.Equals(subFlowType, "P", StringComparison.Ordinal),
            $"activeCorrelations[<subprocess>].subFlowType beklenen 'P' (SubProcess kisa kodu); actual = '{subFlowType ?? "<null>"}'."
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
