using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// SubFlow Error Mapping - Tests SubFlow error handling with error propagation
/// </summary>
public class SubFlowErrorMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var httpTask = task as HttpTask;
            if (httpTask == null)
            {
                throw new InvalidOperationException("Task must be an HttpTask");
            }

            var requestBody = new
            {
                testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
                testType = "subflow-error",
                timestamp = DateTime.UtcNow,
                message = "Testing SubFlow error propagation to parent workflow"
            };

            httpTask.SetBody(requestBody);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "subflow-error-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;
            
            return new ScriptResponse
            {
                Key = "subflow-error-handled",
                Data = new
                {
                    subFlowErrorResult = new
                    {
                        success = false,
                        statusCode = response?.statusCode ?? 500,
                        errorPropagated = true,
                        testType = "SubFlow Error Propagation Test",
                        completedAt = DateTime.UtcNow
                    }
                },
                Tags = new[] { "error-boundary-test", "subflow-error" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "subflow-error-exception",
                Data = new { error = ex.Message }
            };
        }
    }
}

