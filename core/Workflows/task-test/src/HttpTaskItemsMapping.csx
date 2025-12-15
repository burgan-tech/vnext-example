using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// HTTP Task Mapping - Makes external API calls
/// Test case: Verify HTTP requests to external services via Mockoon
/// </summary>
public class HttpTaskItemsMapping : IMapping
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

            // Prepare request body
            var requestBody = new
            {
                testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
                workflowId = context.Workflow.Key,
                instanceId = context.Instance?.Id,
                timestamp = DateTime.UtcNow,
                action = "http-task-test"
            };

            httpTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Test-Id"] = context.Instance?.Data?.testId?.ToString(),
                ["X-Request-Id"] = Guid.NewGuid().ToString(),
                ["X-Correlation-Id"] = context.Instance.Id.ToString()
            };
            httpTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "http-task-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;
            var statusCode = response?.statusCode ?? 500;

            if (statusCode >= 200 && statusCode < 300)
            {
                return new ScriptResponse
                {
                    Key = "http-task-success",
                    Data = new
                    {
                        httpTaskItemsResult = new
                        {
                            success = true,
                            statusCode = statusCode,
                            data = response?.data,
                            executionTime = response?.executionDurationMs,
                            processedAt = DateTime.UtcNow,
                            taskType = "HttpTask"
                        }
                    },
                    Tags = new[] { "task-test", "http-task", "success" }
                };
            }

            return new ScriptResponse
            {
                Key = "http-task-failed",
                Data = new
                {
                    httpTaskItemsResult = new
                    {
                        success = false,
                        statusCode = statusCode,
                        error = response?.errorMessage ?? "HTTP request failed",
                        failedAt = DateTime.UtcNow,
                        taskType = "HttpTask"
                    }
                },
                Tags = new[] { "task-test", "http-task", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "http-task-exception",
                Data = new
                {
                    httpTaskItemsResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "HttpTask"
                    }
                }
            };
        }
    }
}

