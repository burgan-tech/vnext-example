using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary Notify Mapping - Tests notify action with notification config
/// </summary>
public class ErrorBoundaryNotifyMapping : IMapping
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
            testType = "notify-action",
            timestamp = DateTime.UtcNow,
            message = "Testing Notify action with NotificationConfig"
        };

        httpTask.SetBody(requestBody);

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = context.Body;

        return new ScriptResponse
        {
            Key = "notify-success",
            Data = new
            {
                notifyActionResult = new
                {
                    success = response?.isSuccess ?? true,
                    statusCode = response?.statusCode ?? 200,
                    testType = "Notify Action Test",
                    completedAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "notify-action" }
        };
    }
}

