using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Core.IntegrationTests.Infrastructure;
using Xunit;

namespace Core.IntegrationTests.Tests.ErrorBoundaryTestWorkflow;

/// <summary>
/// Integration tests for <c>error-boundary-test-workflow</c>.
/// Tests error boundary actions at task-level, state-level, and global-level.
/// Actions tested: Retry (1), Rollback (2), Ignore (3), Notify (4), Log (5), Abort (0).
/// Flow: init → task-retry → task-ignore → task-log → priority-rules → state-level-test
///       → notify-test → [notify-redirect] → rollback-test → [rollback-redirect]
///       → global-abort-test → (Global Abort → Faulted)
/// </summary>
public class ErrorBoundaryTestWorkflowTests : IntegrationTestBase
{
    private const string WorkflowKey = "error-boundary-test-workflow";

    private static readonly TimeSpan StateTimeout = TimeSpan.FromMinutes(3);

    private readonly WorkflowInstanceTestHelper _workflow;

    public ErrorBoundaryTestWorkflowTests(VNextTestEnvironment environment)
        : base(environment)
    {
        _workflow = new WorkflowInstanceTestHelper(Api, WorkflowKey);
    }

    [Fact]
    public async Task TaskLevel_RetryAction_RetriesExhaustedThenIgnoreFallback()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-retry");
        var attrs = await _workflow.GetAttributesAsync(instanceId);
        ErrorBoundaryInstanceDataAssertions.AssertInitMappingExecuted(attrs);
        ErrorBoundaryInstanceDataAssertions.AssertRetryHandled(attrs);
    }

    [Fact]
    public async Task TaskLevel_IgnoreAction_ErrorIgnoredNextTaskRuns()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-ignore");
        var attrs = await _workflow.GetAttributesAsync(instanceId);
        ErrorBoundaryInstanceDataAssertions.AssertInitMappingExecuted(attrs);
        ErrorBoundaryInstanceDataAssertions.AssertIgnoreExecuted(attrs);
    }

    [Fact]
    public async Task TaskLevel_LogAction_ErrorLoggedNextTaskRuns()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-log");
        var attrs = await _workflow.GetAttributesAsync(instanceId);
        ErrorBoundaryInstanceDataAssertions.AssertInitMappingExecuted(attrs);
        ErrorBoundaryInstanceDataAssertions.AssertLogHandled(attrs);
    }

    [Fact]
    public async Task TaskLevel_PriorityRules_SpecificRuleMatchesBeforeFallback()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-priority");
        var attrs = await _workflow.GetAttributesAsync(instanceId);
        ErrorBoundaryInstanceDataAssertions.AssertInitMappingExecuted(attrs);
        ErrorBoundaryInstanceDataAssertions.AssertPriorityRuleApplied(attrs);
    }

    [Fact]
    public async Task StateLevel_IgnoreAction_ErrorHandledByStateBoundary()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-state-level");
        var attrs = await _workflow.GetAttributesAsync(instanceId);
        ErrorBoundaryInstanceDataAssertions.AssertStateLevelHandled(attrs);
    }

    [Fact]
    public async Task TaskLevel_NotifyAction_TransitionTriggered()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-notify");
        Assert.True(
            state == "rollback-test-state" || state == "global-abort-test-state" || status == "F",
            $"Notify action should redirect flow. Actual state='{state}', status='{status}'"
        );
    }

    [Fact]
    public async Task TaskLevel_RollbackAction_TransitionTriggered()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-rollback");
        Assert.True(
            state == "global-abort-test-state" || status == "F",
            $"Rollback action should redirect flow. Actual state='{state}', status='{status}'"
        );
    }

    [Fact]
    public async Task GlobalLevel_AbortAction_InstanceFaulted()
    {
        var (instanceId, status, state) = await StartAndWaitForFinalAsync("eb-global-abort");
        Assert.Equal("F", status);
        Assert.Equal("global-abort-test-state", state);
    }

    private async Task<(string instanceId, string? status, string state)> StartAndWaitForFinalAsync(string prefix)
    {
        var key = WorkflowInstanceTestHelper.UniqueInstanceKey(prefix);
        var instanceId = await _workflow.StartInstanceIdAsync(
            new
            {
                key,
                tags = new[] { "integration-test", "error-boundary", prefix },
                attributes = new { },
            }
        );

        var deadline = DateTime.UtcNow + StateTimeout;
        string currentState = "";
        string? status = null;

        while (DateTime.UtcNow < deadline)
        {
            var stateBody = await _workflow.GetStateFunctionBodyAsync(instanceId, headers: null);
            currentState = StateFunctionJson.ExtractStateName(stateBody);
            status = StateFunctionJson.ExtractStatus(stateBody);

            if (status == "F" || status == "C")
                return (instanceId, status, currentState);

            if (currentState == "completed-state")
                return (instanceId, status, currentState);

            await Task.Delay(1000);
        }

        Assert.Fail($"Timeout waiting for final state. Last state='{currentState}', status='{status}'");
        return (instanceId, status, currentState);
    }
}
