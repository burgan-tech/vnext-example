using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary Init Mapping - Initializes the error boundary test workflow
/// </summary>
public class ErrorBoundaryInitMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inputData = context.Instance?.Data;

        return new ScriptResponse
        {
            Key = "init-success",
            Data = new
            {
                testId = inputData?.testId ?? Guid.NewGuid().ToString(),
                workflowStartedAt = DateTime.UtcNow,
                testConfiguration = new
                {
                    testTaskBoundary = true,
                    testStateBoundary = true,
                    testSubFlowBoundary = true,
                    testRetryPolicies = true,
                    testTimeoutPolicy = true,
                    testNotifyAction = true
                },
                errorBoundaryTestResults = new { }
            },
            Tags = new[] { "error-boundary-test", "initialized" }
        };
    }
}

