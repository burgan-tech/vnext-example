using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.LifecycleTransitionsTestWorkflow;

/*
* Bug olabilecek noktalar
* - cancel: availableIn: [] içerisine wf cli'ı yazılan state'leri error olarak
*  algılıyor. Runtime tarafı nasıl bilemiyorum
* - bir state'ten başka bir state'e transtion çalıştırıldığında
* diğer state'in auto transition'u varsa ve bu otomatik çalışınca
* başka state'e geçiyorsa ve son geçilen state'in hatalı transition mapping code'u
* varsa status F oluyor ve son state geçişi gösterilmiyor. Ne demek istiyorum:
* A -> B -> C -> D
* A'dan B'ye manuel geçiş. B auto geçişi var. C'ye geçiyor problem yok gibi.
* C'nin D'ye transition'u var ve bu transition'a bağlı mapping'de problem var.
* workflow deployunda felan problem çıkmyıor. A'dan B'ye transition olduğunda
* state B'de gösteriliyor ama aslında C ile D arasındaki transition'dan dolayı uygulama
* F status'e düşüyor. workflow B durumunda F status ile duruyor kalıyor.
! Kısacası feedback yanlış oluyor. state consistency yanlış oluyor.
*
*/

/*
! Buglar:
! Cancel
! Exit
! Reschedule (reschedule-timer sonrasi scheduled transition yeniden kurulmuyor; instance auto-passed-state'te kaliyor)
*/

/// <summary>
/// Integration tests aligned with <c>api-tests/lifecycle-transitions/lifecycle-transitions-test-workflow.http</c>.
/// Helpers live in <see cref="Core.IntegrationTests.Helpers"/>.
/// </summary>
public class LifecycleTransitionsTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "lifecycle-transitions-test-workflow";

    private readonly WorkflowInstanceTestHelper _workflow;

    public LifecycleTransitionsTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _workflow = new WorkflowInstanceTestHelper(Api, WorkflowKey);
    }

    [Fact]
    public async Task HappyPath_Pass_ReachesCompletedState()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-pass-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "pass-path" },
                attributes = new { testPath = "pass" },
            }
        );

        await _workflow.AssertStateAsync(instanceId, "initialize-state");

        await _workflow.RunTransitionAsync(
            instanceId,
            "move-to-processing",
            WorkflowTestHttpHeaders.Role("test-operator")
        );

        await _workflow.AssertStateAsync(instanceId, "auto-passed-state");

        await _workflow.WaitForStateAsync(
            instanceId,
            "pre-complete-state",
            TimeSpan.FromSeconds(15)
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "complete-workflow",
            WorkflowTestHttpHeaders.Role("test-approver")
        );

        await _workflow.AssertStateAsync(instanceId, "completed-state");
    }

    [Fact]
    public async Task FailPath_ReachesAutoFailedState()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-fail-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "fail-path" },
                attributes = new { testPath = "fail" },
            }
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "move-to-processing",
            WorkflowTestHttpHeaders.Role("test-operator")
        );

        await _workflow.AssertStateAsync(instanceId, "auto-failed-state");
    }

    [Fact]
    public async Task DefaultAutoPath_UnknownTestPath_ReachesCompletedState()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-default-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "default-path" },
                attributes = new { testPath = "unknown" },
            }
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "move-to-processing",
            WorkflowTestHttpHeaders.Role("test-operator")
        );

        await _workflow.WaitForStateAsync(instanceId, "completed-state", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task IdempotentStart_SameKey_ReturnsSameInstanceId()
    {
        var key = $"lifecycle-idempotent-{Guid.NewGuid():N}";
        var id1 = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "idempotent" },
                attributes = new { testPath = "pass" },
            }
        );

        var id2 = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test" },
                attributes = new { testPath = "pass" },
            }
        );

        Assert.Equal(id1, id2);
    }

    // TODO: Bu test şu anda başarısız — runtime/platform tarafında cancel ile ilgili bug var; workflow tanımı doğru kabul ediliyor.
    [Fact]
    public async Task CancelTransition_ReachesTerminatedState()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-cancel-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "cancel" },
                attributes = new { testPath = "pass" },
            }
        );

        await _workflow.RunTransitionAsync(instanceId, "cancel-workflow", headers: null);

        await _workflow.AssertStateAsync(instanceId, "terminated-state");
    }

    // TODO: Bu test şu anda başarısız — runtime/platform tarafında exit ile ilgili bug var; workflow tanımı doğru kabul ediliyor.
    [Fact]
    public async Task ExitTransition_FromInitialize_SetsExitExecutedOnInstance()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("exit-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "exit-test" },
                attributes = new { testId = "exit-test" },
            }
        );

        await _workflow.RunTransitionAsync(instanceId, "exit-workflow", headers: null);

        await _workflow.AssertStateAsync(instanceId, "terminated-state");

        var instanceResponse = await Api.GetInstanceAsync(WorkflowKey, instanceId);
        var body = instanceResponse.Body;

        Assert.True(
            body.TryGetProperty("attributes", out var attributes),
            "GetInstance response should include attributes"
        );

        Assert.True(
            attributes.TryGetProperty("exitExecuted", out var exitExecuted)
                && exitExecuted.ValueKind == JsonValueKind.True,
            "Exit mapping should set attributes.exitExecuted = true"
        );
    }

    [Fact]
    public async Task ScheduleCancel_ManualTransition_CancelsTimerAndReachesPreComplete()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("schedule-cancel-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "schedule-cancel-test" },
                attributes = new { testId = "schedule-cancel-test", testPath = "pass" },
            }
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "move-to-processing",
            WorkflowTestHttpHeaders.Role("test-operator")
        );

        await _workflow.AssertStateAsync(instanceId, "auto-passed-state");

        await _workflow.RunTransitionAsync(instanceId, "cancel-schedule-manually", headers: null);

        await _workflow.AssertStateAsync(instanceId, "pre-complete-state");
    }

    // TODO / BUG: reschedule-timer çalışınca workflow tanımına göre scheduled-timer-transition yeniden kurulmalı (~10s sonra timer-triggered → pre-complete).
    // Şu an runtime’da timer yeniden schedule olmuyor; instance sürekli auto-passed-state’te kalıyor — WaitForStateAsync(pre-complete) zaman aşımına düşer.
    // Workflow JSON / reschedule-timer → auto-passed-state ($self) doğru kabul ediliyor; platform tarafı incelenmeli.
    /// <summary>
    /// reschedule-timer: $self ile hâlâ auto-passed-state; ardından zamanlayıcı yeniden kurulmalı (ShortTimerMapping +10s).
    /// Beklenen: scheduled-timer-transition -> timer-triggered-state -> auto-to-pre-complete -> pre-complete-state.
    /// </summary>
    [Fact]
    public async Task RescheduleTimer_SelfTransition_ThenWaitsForRescheduledTimer_ReachesPreCompleteState()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("reschedule-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "reschedule-test" },
                attributes = new { testId = "reschedule-test", testPath = "pass" },
            }
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "move-to-processing",
            WorkflowTestHttpHeaders.Role("test-operator")
        );

        await _workflow.AssertStateAsync(instanceId, "auto-passed-state");

        await _workflow.RunTransitionAsync(instanceId, "reschedule-timer", headers: null);

        await _workflow.AssertStateAsync(instanceId, "auto-passed-state");

        // Yeniden kurulan timer ~10 sn; timer-triggered onEntries + auto gecis icin pay
        await _workflow.WaitForStateAsync(
            instanceId,
            "pre-complete-state",
            TimeSpan.FromSeconds(20)
        );
    }

    [Fact]
    public async Task QueryRoles_WorkflowLevel_AllowsTestViewerOnInitialize()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("roles-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "roles-test" },
                attributes = new { testId = "roles-test", testPath = "pass" },
            }
        );

        await _workflow.AssertAuthorizeQueryRolesAsync(
            instanceId,
            "test-viewer",
            expectAllowed: true
        );
    }

    [Fact]
    public async Task QueryRoles_OnProcessingState_AllowsProcessor_DeniesViewerWhenObserved()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("roles-processing-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "roles-test" },
                attributes = new { testId = "roles-test", testPath = "pass" },
            }
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "move-to-processing",
            WorkflowTestHttpHeaders.Role("test-operator")
        );

        var observed = await _workflow.PollStateUntilAnyAsync(
            instanceId,
            TimeSpan.FromSeconds(2),
            new HashSet<string>(StringComparer.Ordinal) { "processing-state", "auto-passed-state" }
        );

        if (observed == "processing-state")
        {
            await _workflow.AssertAuthorizeQueryRolesAsync(
                instanceId,
                "test-processor",
                expectAllowed: true
            );
            await _workflow.AssertAuthorizeQueryRolesAsync(
                instanceId,
                "test-viewer",
                expectAllowed: false
            );
        }
        else
        {
            Assert.Equal("auto-passed-state", observed);
        }
    }

    [Fact]
    public async Task StateFunction_WithTestOperatorRole_IncludesMoveToProcessingInTransitions()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-state-filter-allow");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "state-filter" },
                attributes = new { testPath = "pass" },
            }
        );

        await _workflow.AssertStateAsync(instanceId, "initialize-state");

        var body = await _workflow.GetStateFunctionBodyAsync(
            instanceId,
            WorkflowTestHttpHeaders.Role("test-operator")
        );
        Assert.True(
            StateFunctionJson.TransitionsContainKey(body, "move-to-processing"),
            "State response transitions[] should include an item with name \"move-to-processing\" when role matches transition roles (v0.0.39+ filtering)."
        );
    }

    [Fact]
    public async Task StateFunction_WithTestViewerRole_ExcludesMoveToProcessingFromTransitions()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-state-filter-deny");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "state-filter" },
                attributes = new { testPath = "pass" },
            }
        );

        await _workflow.AssertStateAsync(instanceId, "initialize-state");

        var body = await _workflow.GetStateFunctionBodyAsync(
            instanceId,
            WorkflowTestHttpHeaders.Role("test-viewer")
        );
        Assert.False(
            StateFunctionJson.TransitionsContainKey(body, "move-to-processing"),
            "State response transitions[] should not list name \"move-to-processing\" when caller role is not in transition roles allow list."
        );
    }
}
