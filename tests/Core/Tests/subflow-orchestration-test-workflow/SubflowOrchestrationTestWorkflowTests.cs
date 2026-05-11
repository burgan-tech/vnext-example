using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.SubflowOrchestrationTestWorkflow;

/*
! Bugs:
! UpdateData: parent endpoint should resolve child workflow updateData while child SubFlow is active.
! Runtime currently returns Instance:100024 ("subflow-orchestration-parent does not define an updateData configuration").
*/

/// <summary>
/// Integration tests aligned with <c>api-tests/subflow-orchestration/subflow-orchestration.http</c>.
/// Covers parent-child-grandchild SubFlow routing, shared transitions, updateData, cancel cascade,
/// and effective state exposure through the parent instance.
/// </summary>
public class SubflowOrchestrationTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "subflow-orchestration-parent";

    private static readonly TimeSpan StateTimeout = TimeSpan.FromMinutes(1);

    private readonly WorkflowInstanceTestHelper _wf;
    private readonly SubflowOrchestrationScenarioActions _scenario;

    public SubflowOrchestrationTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _wf = new WorkflowInstanceTestHelper(Api, WorkflowKey);
        _scenario = new SubflowOrchestrationScenarioActions(_wf, StateTimeout);
    }

    [Fact]
    public async Task HappyPath_ParentChildGrandchild_ReachesParentCompleted()
    {
        const string testId = "subflow-orch-happy-path";
        var instanceId = await _scenario.StartAsync(testId, "happy-path");

        await _scenario.ProceedToGrandchildAsync(instanceId);

        await _scenario.CompleteGrandchildAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.ParentCompletedState,
            StateFunctionJson.ExtractStateName(stateBody)
        );
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));

        var attributes = await _wf.GetAttributesAsync(instanceId);
        SubflowOrchestrationInstanceDataAssertions.AssertHappyPathCompleted(attributes, testId);
    }

    [Fact]
    public async Task ParentCommonTransition_InParentSubflowState_MarksParentDataOnly()
    {
        var instanceId = await _scenario.StartAsync(
            "subflow-orch-parent-common-transition",
            "parent-common-transition"
        );

        await _scenario.RunParentCommonTransitionAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.ChildManualState,
            StateFunctionJson.ExtractStateName(stateBody)
        );

        var attributes = await _wf.GetAttributesAsync(instanceId);
        SubflowOrchestrationInstanceDataAssertions.AssertParentCommonTransitionExecuted(attributes);
    }

    [Fact]
    public async Task ChildSharedTransition_InAllowedManualState_MergesMarkerAfterCompletion()
    {
        var instanceId = await _scenario.StartAsync(
            "subflow-orch-child-shared-mark",
            "shared-transition"
        );

        var stateBeforeShared = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.ChildManualState,
            StateFunctionJson.ExtractStateName(stateBeforeShared)
        );
        if (!StateFunctionJson.TransitionsContainKey(stateBeforeShared, "shared-child-mark"))
        {
            Assert.Fail(
                "TODO: BUG: shared-child-mark should be available while effective state is child-manual-state. "
                    + "subflow-orchestration-child.sharedTransitions.availableIn includes child-manual-state, "
                    + "but functions/state does not expose the shared transition."
            );
        }

        await _scenario.RunChildSharedMarkAsync(instanceId);

        var stateAfterShared = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.ChildManualState,
            StateFunctionJson.ExtractStateName(stateAfterShared)
        );

        var attributes = await _wf.GetAttributesAsync(instanceId);
        SubflowOrchestrationInstanceDataAssertions.AssertPropertyMissing(
            attributes,
            "childUpdatedParent",
            "shared-child-mark must not update parent data through updateData."
        );

        await _scenario.ProceedToGrandchildAsync(instanceId);
        await _scenario.CompleteGrandchildAsync(instanceId);

        var completedAttributes = await _wf.GetAttributesAsync(instanceId);
        SubflowOrchestrationInstanceDataAssertions.AssertChildSharedMarkExecuted(
            completedAttributes
        );
        SubflowOrchestrationInstanceDataAssertions.AssertPropertyMissing(
            completedAttributes,
            "childUpdatedParent",
            "shared-child-mark must remain separate from updateData after child output is merged."
        );
    }

    [Fact]
    public async Task ChildSharedTransition_InDisallowedSubflowState_DoesNotExecute()
    {
        var instanceId = await _scenario.StartAsync(
            "subflow-orch-child-shared-disallowed",
            "shared-transition-disallowed"
        );
        await _scenario.ProceedToGrandchildAsync(instanceId);

        // Runtime may reject the transition at HTTP level or accept it as a no-op.
        // The contract under test is that it must not execute outside availableIn.
        _ = await _scenario.TryRunChildSharedMarkFromCurrentStateAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.GrandchildInitialState,
            StateFunctionJson.ExtractStateName(stateBody)
        );

        var dataBody = await _wf.CallFunctionAsync(instanceId, "data");
        SubflowOrchestrationInstanceDataAssertions.AssertChildSharedMarkNotExecuted(dataBody);

        await _scenario.CompleteGrandchildAsync(instanceId);
    }

    [Fact]
    public async Task EffectiveState_AfterProceedToSubflow_ExposesGrandchildInitial()
    {
        var instanceId = await _scenario.StartAsync(
            "subflow-orch-effective-state",
            "effective-state"
        );

        await _scenario.ProceedToGrandchildAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.GrandchildInitialState,
            StateFunctionJson.ExtractStateName(stateBody)
        );
        Assert.True(
            StateFunctionJson.TransitionsContainKey(stateBody, "complete-grandchild"),
            "State function should expose complete-grandchild while grandchild-initial is the effective state."
        );
    }

    // TODO / BUG: Child workflow defines updateData (update-parent-data) as required for SubFlow context,
    // but PATCH through the parent instance currently resolves only the parent workflow and returns
    // Instance:100024 ("subflow-orchestration-parent does not define an updateData configuration").
    // Workflow definition is assumed correct per vnext-workflow-creation §13.2; platform/runtime needs investigation.
    [Fact]
    public async Task UpdateData_FromChildSubflow_UpdatesParentData()
    {
        var instanceId = await _scenario.StartAsync("subflow-orch-update-data", "update-data");
        await _scenario.ProceedToGrandchildAsync(instanceId);

        await _scenario.RunUpdateParentDataAsync(instanceId, "from-child-updatedata");

        var attributes = await _wf.GetAttributesAsync(instanceId);
        SubflowOrchestrationInstanceDataAssertions.AssertUpdateDataApplied(attributes);

        await _scenario.CompleteGrandchildAsync(instanceId);
    }

    [Fact]
    public async Task CancelParent_WhileGrandchildActive_ReachesParentCancelled()
    {
        var instanceId = await _scenario.StartAsync(
            "subflow-orch-cancel-cascade",
            "cancel-cascade"
        );
        await _scenario.ProceedToGrandchildAsync(instanceId);

        await _scenario.CancelParentAsync(instanceId);

        var stateBody = await _wf.GetStateFunctionBodyAsync(instanceId, headers: null);
        Assert.Equal(
            SubflowOrchestrationScenarioActions.ParentCancelledState,
            StateFunctionJson.ExtractStateName(stateBody)
        );
        Assert.Equal("C", StateFunctionJson.ExtractStatus(stateBody));
    }
}
