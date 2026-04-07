using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary Retry Mapping - Tests Task-level retry with exponential backoff
/// </summary>
public class ErrorBoundaryRetryMapping : IMapping
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
            testType = "task-boundary-retry",
            timestamp = DateTime.UtcNow,
            message = "Testing Task-level ErrorBoundary with Retry action"
        };

        httpTask.SetBody(requestBody)

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = context.Body;

        return new ScriptResponse
        {
            Key = "retry-success",
            Data = new
            {
                taskBoundaryRetryResult = new
                {
                    success = response?.isSuccess ?? false,
                    statusCode = response?.statusCode,
                    testType = "Task-Level ErrorBoundary Retry",
                    completedAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "task-boundary", "retry" }
        };
    }
}

