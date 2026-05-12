using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;

namespace Core.IntegrationTests.Tests.LifecycleTransitionsTestWorkflow;

/*
! Bugs:
! Cancel
! Exit
! Reschedule (after reschedule-timer the scheduled transition is not re-armed; instance stays on auto-passed-state)
*/

/// <summary>
/// Integration tests aligned with <c>api-tests/lifecycle-transitions/lifecycle-transitions-test-workflow.http</c>.
/// Shared API helpers: <see cref="Core.IntegrationTests.Helpers"/>; workflow-specific data contract:
/// <see cref="LifecyclePassPathInstanceDataAssertions"/>.
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
            TimeSpan.FromSeconds(10)
        );

        await _workflow.RunTransitionAsync(
            instanceId,
            "complete-workflow",
            WorkflowTestHttpHeaders.Role("test-approver")
        );

        await _workflow.AssertStateAsync(instanceId, "completed-state");
    }

    /// <summary>
    /// On the pass path that reaches <c>pre-complete-state</c> via timer, verifies instance data fields written by
    /// <b>onEntry / onExit</b> (and transition) script tasks match expectations — contract-level proof tasks ran.
    /// </summary>
    /// <remarks>
    /// Field names match <c>core/Workflows/lifecycle-transitions/src/*.csx</c>; update this test deliberately if scripts change.
    /// </remarks>
    [Fact]
    public async Task PassPath_InstanceData_AfterPreCompleteViaTimer_ReflectsOnEntryOnExitScripts()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("lifecycle-pass-data-contract");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "lifecycle", "pass-data-contract" },
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
            TimeSpan.FromSeconds(10)
        );

        var instanceAtPreComplete = await Api.GetInstanceAsync(WorkflowKey, instanceId);
        Assert.True(
            instanceAtPreComplete.Body.TryGetProperty("attributes", out var attrs),
            "GetInstance should include attributes"
        );

        LifecyclePassPathInstanceDataAssertions.AssertAfterPreCompleteViaTimer(attrs);
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

    // TODO: Currently failing — cancel bug on runtime/platform side; workflow definition assumed correct.
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

    // TODO: Currently failing — exit bug on runtime/platform side; workflow definition assumed correct.
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

    // TODO: Currently failing — exit bug on runtime/platform side; workflow definition assumed correct.
    [Fact]
    public async Task ExitTransition_FromPreComplete_SetsExitExecutedOnInstance()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("exit-precomplete-test");
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "exit-precomplete-test" },
                attributes = new { testId = "exit-precomplete-test", testPath = "pass" },
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

    // TODO / BUG: After reschedule-timer the definition expects scheduled-timer-transition to be re-armed (~6s then timer-triggered → pre-complete).
    // Runtime does not reschedule; instance stays on auto-passed-state — WaitForStateAsync(pre-complete) times out.
    // Workflow JSON reschedule-timer → auto-passed-state ($self) assumed correct; platform needs investigation.
    /// <summary>
    /// reschedule-timer: stays on auto-passed-state via $self; timer should reschedule (ShortTimerMapping +6s).
    /// Expected: scheduled-timer-transition -&gt; timer-triggered-state -&gt; auto-to-pre-complete -&gt; pre-complete-state.
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

        // Rescheduled timer ~6s; slack for timer-triggered onEntries + auto transition
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
    public async Task QueryRoles_OnPreCompleteState_AllowsProcessor_DeniesViewer()
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey("roles-precomplete-test");
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

        await _workflow.AssertStateAsync(instanceId, "auto-passed-state");

        await _workflow.RunTransitionAsync(instanceId, "cancel-schedule-manually", headers: null);

        await _workflow.AssertStateAsync(instanceId, "pre-complete-state");

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
            "State response transitions[] should list \"move-to-processing\" when caller role is in transition allow list (v0.0.39+ list filtering only; PATCH is not role-gated by this list)."
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
            "State response transitions[] should omit \"move-to-processing\" when caller role is not in allow list (UI/list filtering); same transition may still be invokable via PATCH depending on runtime/API policy."
        );
    }
}
