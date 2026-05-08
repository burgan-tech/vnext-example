using Core.IntegrationTests.Helpers;

namespace Core.IntegrationTests.Tests.VersionConsistencyTestWorkflow;

/// <summary>
/// Scenario actions for <c>version-consistency-test-workflow</c>.
/// Encapsulates instance start and state transitions for both v1 and v2 paths.
/// </summary>
public sealed class VersionConsistencyScenarioActions
{
    private readonly WorkflowInstanceTestHelper _wf;
    private readonly TimeSpan _timeout;

    public VersionConsistencyScenarioActions(WorkflowInstanceTestHelper wf, TimeSpan timeout)
    {
        _wf = wf;
        _timeout = timeout;
    }

    /// <summary>
    /// Starts an instance and waits for auto transition to land on <c>processing-state</c>.
    /// On v2 path: init-state → (auto) → processing-state.
    /// </summary>
    public async Task<string> StartAndWaitForProcessingAsync(string? testId = null)
    {
        var id = await _wf.StartInstanceIdAsync(
            new
            {
                key = WorkflowInstanceTestHelper.UniqueInstanceKey("version-consistency"),
                tags = new[] { "integration-test", "version-consistency" },
                attributes = new { testId = testId ?? $"vc-{Guid.NewGuid():N}" },
            }
        );
        await _wf.WaitForStateAsync(id, "processing-state", _timeout);
        return id;
    }

    /// <summary>
    /// Starts an instance with a specific key and waits for <c>processing-state</c>.
    /// Used for idempotent start tests.
    /// </summary>
    public async Task<string> StartWithKeyAndWaitForProcessingAsync(string key, string? testId = null)
    {
        var id = await _wf.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "version-consistency" },
                attributes = new { testId = testId ?? $"vc-{Guid.NewGuid():N}" },
            }
        );
        await _wf.WaitForStateAsync(id, "processing-state", _timeout);
        return id;
    }

    /// <summary>
    /// Runs <c>complete-processing</c> transition. On v2 path lands on <c>review-state</c>.
    /// </summary>
    public async Task AdvanceToReviewAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "complete-processing", headers: null);
        await _wf.WaitForStateAsync(instanceId, "review-state", _timeout);
    }

    /// <summary>
    /// Runs <c>approve-review</c> transition → <c>completed-state</c>.
    /// </summary>
    public async Task ApproveReviewAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "approve-review", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", _timeout);
    }

    /// <summary>
    /// Full v2 happy path: start → processing → review → completed.
    /// Returns instance id.
    /// </summary>
    public async Task<string> RunFullV2HappyPathAsync(string? testId = null)
    {
        var id = await StartAndWaitForProcessingAsync(testId);
        await AdvanceToReviewAsync(id);
        await ApproveReviewAsync(id);
        return id;
    }

    /// <summary>
    /// V1 path: <c>complete-processing</c> → directly <c>completed-state</c> (no review).
    /// Only works when instance was started on v1.0.0.
    /// </summary>
    public async Task CompleteV1PathAsync(string instanceId)
    {
        await _wf.RunTransitionAsync(instanceId, "complete-processing", headers: null);
        await _wf.WaitForStateAsync(instanceId, "completed-state", _timeout);
    }
}
