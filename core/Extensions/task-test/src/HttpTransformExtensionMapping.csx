using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// HTTP Transform Extension Mapping - Transforms and enriches HTTP response data
/// Test case: Verify data transformation and enrichment in extensions
/// </summary>
public class HttpTransformExtensionMapping : IMapping
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

            // Prepare request body with extension context
            var requestBody = new
            {
                source = "extension",
                extensionName = "test-http-transform-extension",
                workflowId = context.Workflow.Key,
                instanceId = context.Instance?.Id,
                timestamp = DateTime.UtcNow,
                requestType = "extension-transform-test",
                userData = new
                {
                    userId = context.Instance?.Data?.testId,
                    session = "test-session",
                    operation = "transform-data"
                }
            };

            httpTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Extension-Name"] = "test-http-transform-extension",
                ["X-Transform-Type"] = "enrichment",
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
                Key = "http-transform-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    /// <summary>
    /// Transform and enrich the "data" field from HTTP response
    /// - Extract original data
    /// - Transform taskType to uppercase
    /// - Add analytics information
    /// - Enrichment with additional metadata
    /// - Calculate derived values
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
                // Extract original data
                var originalData = response?.data;
                var metadata = response?.data?.metadata;

                // Transform and enrich data
                var transformedData = new
                {
                    // Original data (transformed)
                    taskTypeUpper = originalData?.data?.taskType?.ToString()?.ToUpper(),
                    originalMessage = originalData?.data?.message,
                    receivedPartial = new
                    {
                        source = originalData?.data?.receivedData?.source,
                        extension = originalData?.data?.receivedData?.extensionName
                    },
                    // Add derived values
                    processedTimestamp = DateTime.UtcNow.
                        AddHours(5). // Add 5 hours
                        ToString("O"),
                    customRequestId = $"TRANS-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}",
                    // Add analytics
                    analytics = new
                    {
                        dataSizeBytes = System.Text.Encoding.UTF8.GetByteCount(
                            System.Text.Json.JsonSerializer.Serialize(originalData)
                        ),
                        fieldCount = 3, // taskType, message, receivedData
                        processingTimeMs = metadata?.responseTime,
                        successRate = 100.0
                    },
                    // Add enrichment
                    enrichment = new
                    {
                        category = "API_TEST",
                        priority = "HIGH",
                        tags = new[] { "transformed", "enriched", "analyzed" },
                        verified = true,
                        enrichedAt = DateTime.UtcNow
                    },
                    // Add validation results
                    validation = new
                    {
                        isValid = true,
                        validationErrors = new object[] { },
                        validatedFields = new[] { "taskType", "message", "requestId" },
                        validatedAt = DateTime.UtcNow
                    }
                };

                return new ScriptResponse
                {
                    Key = "test-http-transform-extension-success",
                    Data = new
                    {
                        // Transformed and enriched data
                        transformedData = transformedData,
                        // Original data for reference
                        originalData = originalData,
                        // Metadata
                        extensionMetadata = new
                        {
                            extensionName = "test-http-transform-extension",
                            transformationType = "enrichment+analytics",
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
                Key = "test-http-transform-extension-failed",
                Data = new
                {
                    error = "HTTP request failed",
                    statusCode = statusCode,
                    errorMessage = response?.errorMessage ?? "Unknown error",
                    extensionMetadata = new
                    {
                        extensionName = "test-http-transform-extension",
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
                Key = "test-http-transform-extension-exception",
                Data = new
                {
                    error = ex.Message,
                    extensionMetadata = new
                    {
                        extensionName = "test-http-transform-extension",
                        executedAt = DateTime.UtcNow,
                        success = false
                    }
                }
            };
        }
    }
}

