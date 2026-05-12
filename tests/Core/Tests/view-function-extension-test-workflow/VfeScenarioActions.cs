using Core.IntegrationTests.Helpers;

namespace Core.IntegrationTests.Tests.ViewFunctionExtensionTestWorkflow;

/// <summary>
/// Scenario actions for the <c>view-function-extension-test-workflow</c>.
/// Encapsulates instance start and state transitions.
/// </summary>
public sealed class VfeScenarioActions
{
    private readonly WorkflowInstanceTestHelper _wf;
    private readonly TimeSpan _timeout;

    public VfeScenarioActions(WorkflowInstanceTestHelper wf, TimeSpan timeout)
    {
        _wf = wf;
        _timeout = timeout;
    }

    /// <summary>
    /// Starts a new workflow instance. Instance lands on view-test-state (no auto transition).
    /// </summary>
    public async Task<string> StartAndAssertViewTestStateAsync()
    {
        var id = await _wf.StartInstanceIdAsync(
            new
            {
                key = WorkflowInstanceTestHelper.UniqueInstanceKey("vfe-test"),
                tags = new[] { "integration-test", "view-function-extension" },
                attributes = new { },
            }
        );
        await _wf.WaitForStateAsync(id, "view-test-state", _timeout);
        return id;
    }

    /// <summary>
    /// Starts instance and advances to html-view-state via continue-to-html manual transition.
    /// </summary>
    public async Task<string> StartAndWaitForHtmlViewStateAsync()
    {
        var id = await StartAndAssertViewTestStateAsync();
        await AdvanceToHtmlViewStateAsync(id);
        return id;
    }

    public async Task AdvanceToHtmlViewStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "continue-to-html", headers: null);
        await _wf.AssertStateAsync(instanceId, "html-view-state");
    }

    public async Task AdvanceToMarkdownStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "to-markdown", headers: null);
        await _wf.AssertStateAsync(instanceId, "markdown-view-state");
    }

    public async Task AdvanceToDeeplinkStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "to-deeplink", headers: null);
        await _wf.AssertStateAsync(instanceId, "deeplink-view-state");
    }

    public async Task AdvanceToHttpStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "to-http", headers: null);
        await _wf.AssertStateAsync(instanceId, "http-view-state");
    }

    public async Task AdvanceToUrnViewStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "to-urn", headers: null);
        await _wf.AssertStateAsync(instanceId, "urn-view-state");
    }

    public async Task AdvanceToWizardStepStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "to-wizard", headers: null);
        await _wf.AssertStateAsync(instanceId, "wizard-step-state");
    }

    public async Task AdvanceToCompletedStateAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "complete", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", _timeout);
    }

    /// <summary>
    /// Runs full happy path:
    /// start → view-test → html → markdown → deeplink → http → urn → wizard → completed.
    /// Returns instance ID.
    /// </summary>
    public async Task<string> RunFullHappyPathAsync()
    {
        var id = await StartAndWaitForHtmlViewStateAsync();
        await AdvanceToMarkdownStateAsync(id);
        await AdvanceToDeeplinkStateAsync(id);
        await AdvanceToHttpStateAsync(id);
        await AdvanceToUrnViewStateAsync(id);
        await AdvanceToWizardStepStateAsync(id);
        await AdvanceToCompletedStateAsync(id);
        return id;
    }
}
