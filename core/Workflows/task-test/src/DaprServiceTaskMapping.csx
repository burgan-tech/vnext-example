using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// DaprService Task Mapping - Invokes Dapr services
/// Test case: Verify service-to-service invocation via Dapr
/// </summary>
public class DaprServiceTaskMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var serviceTask = task as DaprServiceTask;
            if (serviceTask == null)
            {
                throw new InvalidOperationException("Task must be a DaprServiceTask");
            }

            // Configure target service
            serviceTask.SetAppId("mockoon");
            serviceTask.SetMethodName("/api/task-test/dapr-endpoint");

            // Prepare request body
            var requestBody = new
            {
                testId = context.Instance?.Data?.testId ?? Guid.NewGuid().ToString(),
                workflowId = context.Workflow.Key,
                instanceId = context.Instance?.Id,
                timestamp = DateTime.UtcNow,
                action = "dapr-service-test"
            };

            serviceTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["X-Test-Id"] = context.Instance?.Data?.testId?.ToString(),
                ["X-Request-Id"] = Guid.NewGuid().ToString(),
                ["X-Correlation-Id"] = context.Instance.Id.ToString()
            };
            serviceTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "dapr-service-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;

            if (response?.isSuccess == true)
            {
                return new ScriptResponse
                {
                    Key = "dapr-service-success",
                    Data = new
                    {
                        daprServiceResult = new
                        {
                            success = true,
                            data = response?.data,
                            executionTime = response?.executionDurationMs,
                            processedAt = DateTime.UtcNow,
                            taskType = "DaprServiceTask"
                        }
                    },
                    Tags = new[] { "task-test", "dapr-service", "success" }
                };
            }

            return new ScriptResponse
            {
                Key = "dapr-service-failed",
                Data = new
                {
                    daprServiceResult = new
                    {
                        success = false,
                        error = response?.errorMessage ?? "Dapr service invocation failed",
                        failedAt = DateTime.UtcNow,
                        taskType = "DaprServiceTask"
                    }
                },
                Tags = new[] { "task-test", "dapr-service", "failed" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "dapr-service-exception",
                Data = new
                {
                    daprServiceResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "DaprServiceTask"
                    }
                }
            };
        }
    }
}

