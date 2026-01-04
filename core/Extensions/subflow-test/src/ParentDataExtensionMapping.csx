using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Parent Data Extension Mapping - Provides additional data for parent workflow view
/// This extension is used to test longpolling functionality for parent workflow views.
/// </summary>
public class ParentDataExtensionMapping : IMapping
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

            // Prepare request body for extension data
            var requestBody = new
            {
                extensionType = "parent-data-extension",
                instanceId = context.Instance?.Id,
                workflowKey = context.Workflow?.Key,
                requestedAt = DateTime.UtcNow
            };

            httpTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Extension-Type"] = "parent-data",
                ["X-Request-Id"] = Guid.NewGuid().ToString()
            };
            httpTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "parent-extension-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            return new ScriptResponse
            {
                Key = "parent-extension-success",
                Data = new
                {
                    parentExtension = new
                    {
                        extensionName = "parent-data-extension",
                        source = "parent-workflow",
                        loadedAt = DateTime.UtcNow,
                        data = new
                        {
                            parentConfig = new
                            {
                                maxWaitTime = 300,
                                retryEnabled = true,
                                notificationLevel = "info"
                            },
                            parentMetadata = new
                            {
                                workflowType = "main",
                                hasActiveSubflow = true,
                                extensionVersion = "1.0.0"
                            }
                        }
                    }
                },
                Tags = new[] { "subflow-test", "parent-extension", "longpolling" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "parent-extension-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

