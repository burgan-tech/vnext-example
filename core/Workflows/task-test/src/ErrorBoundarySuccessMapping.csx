using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// ErrorBoundary Success Mapping - Tests successful execution path
/// </summary>
public class ErrorBoundarySuccessMapping : IMapping
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
            testType = "success-path",
            timestamp = DateTime.UtcNow,
            message = "Testing successful execution path"
        };

        httpTask.SetBody(requestBody);

        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = context.Body;

        return new ScriptResponse
        {
            Key = "success-completed",
            Data = new
            {
                successResult = new
                {
                    success = response?.isSuccess ?? true,
                    statusCode = response?.statusCode ?? 200,
                    testType = "Success Path",
                    completedAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "success" }
        };
    }
}

