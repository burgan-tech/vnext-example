using System.Net.Http;
using Core.IntegrationTests.Helpers;

namespace Core.IntegrationTests.Tests.SubflowOrchestrationTestWorkflow;

/// <summary>
/// Scenario actions for <c>subflow-orchestration-parent</c>.
/// Keeps workflow-specific state and transition literals out of generic helpers.
/// </summary>
internal sealed class SubflowOrchestrationScenarioActions
{
    public const string ChildManualState = "child-manual-state";
    public const string GrandchildInitialState = "grandchild-initial";
    public const string ParentCompletedState = "parent-completed";
    public const string ParentCancelledState = "parent-cancelled";

    private const string ProceedToSubflowTransition = "proceed-to-subflow";
    private const string CompleteGrandchildTransition = "complete-grandchild";
    private const string CancelParentTransition = "cancel-parent";
    private const string ParentCommonTransition = "shared-common-transition";
    private const string ChildSharedMarkTransition = "shared-child-mark";
    private const string UpdateParentDataTransition = "update-parent-data";

    private readonly WorkflowInstanceTestHelper _wf;
    private readonly TimeSpan _timeout;

    public SubflowOrchestrationScenarioActions(
        WorkflowInstanceTestHelper wf,
        TimeSpan timeout
    )
    {
        _wf = wf;
        _timeout = timeout;
    }

    public async Task<string> StartAsync(string testId, params string[] extraTags)
    {
        var tags = new List<string> { "integration-test", "subflow-orchestration" };
        tags.AddRange(extraTags);

        var id = await _wf.StartInstanceIdAsync(
            new
            {
                key = WorkflowInstanceTestHelper.UniqueInstanceKey("subflow-orch-it"),
                tags = tags.ToArray(),
                attributes = new { testId },
            }
        );

        await _wf.WaitForStateAsync(id, ChildManualState, _timeout);
        return id;
    }

    public async Task ProceedToGrandchildAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, ProceedToSubflowTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, GrandchildInitialState, _timeout);
    }

    public async Task CompleteGrandchildAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, CompleteGrandchildTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, ParentCompletedState, _timeout);
    }

    public async Task CancelParentAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, CancelParentTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, ParentCancelledState, _timeout);
    }

    public async Task RunParentCommonTransitionAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, ParentCommonTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, ChildManualState, _timeout);
    }

    public async Task RunChildSharedMarkAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, ChildSharedMarkTransition, headers: null);
        await _wf.WaitForStateAsync(instanceId, ChildManualState, _timeout);
    }

    public async Task<bool> TryRunChildSharedMarkFromCurrentStateAsync(string instanceId)
    {
        try
        {
            await _wf.RunTransitionAsync(instanceId, ChildSharedMarkTransition, headers: null);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task RunUpdateParentDataAsync(string instanceId, string updateMessage)
    {
        await _wf.RunTransitionAsync(
            instanceId,
            UpdateParentDataTransition,
            headers: null,
            new { attributes = new { updateMessage } }
        );
    }
}
