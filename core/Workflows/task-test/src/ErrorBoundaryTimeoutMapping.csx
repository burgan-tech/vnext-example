using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary Timeout Mapping - Tests timeout policy handling
/// </summary>
public class ErrorBoundaryTimeoutMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
        {
            throw new InvalidOperationException("Task must be an HttpTask");
        }

        var requestBody = new
        {
            testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
            testType = "timeout-policy",
            timestamp = DateTime.UtcNow,
            message = "Testing Timeout Policy with state-level ErrorBoundary"
        };

        httpTask.SetBody(requestBody);

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = context.Body;

        return new ScriptResponse
        {
            Key = "timeout-success",
            Data = new
            {
                timeoutPolicyResult = new
                {
                    success = response?.isSuccess ?? true,
                    statusCode = response?.statusCode ?? 200,
                    testType = "Timeout Policy Test",
                    completedAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "timeout-policy" }
        };
    }
}

