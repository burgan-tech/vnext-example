using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary Ignore Mapping - Tests State-level ignore action for specific error codes
/// </summary>
public class ErrorBoundaryIgnoreMapping : IMapping
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
            testType = "state-boundary-ignore",
            timestamp = DateTime.UtcNow,
            message = "Testing State-level ErrorBoundary with Ignore action for 400 errors"
        };

        httpTask.SetBody(requestBody);

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = context.Body;

        return new ScriptResponse
        {
            Key = "ignore-success",
            Data = new
            {
                stateBoundaryIgnoreResult = new
                {
                    success = response?.isSuccess ?? true,
                    statusCode = response?.statusCode,
                    errorIgnored = response?.statusCode == 400,
                    testType = "State-Level ErrorBoundary Ignore",
                    completedAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "state-boundary", "ignore" }
        };
    }
}

