using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// HTTP Data Extension Mapping - Calls HTTP task and extracts response data
/// Test case: Verify extension data in GetInstanceData
/// </summary>
public class HttpDataExtensionMapping : IMapping
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

            // Prepare request body for HTTP task
            var requestBody = new
            {
                source = "extension",
                extensionName = "test-http-data-extension",
                workflowId = context.Workflow.Key,
                instanceId = context.Instance?.Id,
                timestamp = DateTime.UtcNow,
                requestType = "extension-data-fetch"
            };

            httpTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Extension-Name"] = "test-http-data-extension",
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
                Key = "http-extension-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    /// <summary>
    /// Extract and return the "data" field from HTTP response
    /// This data will be available in GetInstanceData under "extensions"
    /// </summary>
    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var response = context.Body;
            var statusCode = response?.statusCode ?? 500;

            // Check if HTTP request was successful
            if (statusCode >= 200 && statusCode < 300)
            {
                // Extract the "data" field from response
                var httpResponseData = response?.data;

                return new ScriptResponse
                {
                    Key = "test-http-data-extension-success",
                    Data = new
                    {
                        // This is the data that will be available in extensions
                        httpData = httpResponseData,
                        extensionMetadata = new
                        {
                            extensionName = "test-http-data-extension",
                            executedAt = DateTime.UtcNow,
                            statusCode = statusCode,
                            success = true
                        }
                    }
                };
            }

            // HTTP request failed
            return new ScriptResponse
            {
                Key = "test-http-data-extension-failed",
                Data = new
                {
                    error = "HTTP request failed",
                    statusCode = statusCode,
                    errorMessage = response?.errorMessage ?? "Unknown error",
                    extensionMetadata = new
                    {
                        extensionName = "test-http-data-extension",
                        executedAt = DateTime.UtcNow,
                        success = false
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "test-http-data-extension-exception",
                Data = new
                {
                    error = ex.Message,
                    extensionMetadata = new
                    {
                        extensionName = "test-http-data-extension",
                        executedAt = DateTime.UtcNow,
                        success = false
                    }
                }
            };
        }
    }
}

