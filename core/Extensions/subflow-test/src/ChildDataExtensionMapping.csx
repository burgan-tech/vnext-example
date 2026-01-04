using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Child Data Extension Mapping - Provides additional data for child workflow view
/// This extension is used to test longpolling functionality for child workflow views.
/// </summary>
public class ChildDataExtensionMapping : IMapping
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
                extensionType = "child-data-extension",
                instanceId = context.Instance?.Id,
                workflowKey = context.Workflow?.Key,
                parentInstanceId = context.Instance?.Data?.parentInstanceId,
                requestedAt = DateTime.UtcNow
            };

            httpTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Extension-Type"] = "child-data",
                ["X-Request-Id"] = Guid.NewGuid().ToString()
            };
            httpTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "child-extension-input-error",
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
                Key = "child-extension-success",
                Data = new
                {
                    childExtension = new
                    {
                        extensionName = "child-data-extension",
                        source = "child-workflow",
                        loadedAt = DateTime.UtcNow,
                        data = new
                        {
                            childConfig = new
                            {
                                isSubflow = true,
                                blocksParent = true,
                                completionRequired = true
                            },
                            childMetadata = new
                            {
                                workflowType = "subflow",
                                parentWorkflow = "subflow-view-test-parent",
                                extensionVersion = "1.0.0"
                            }
                        }
                    }
                },
                Tags = new[] { "subflow-test", "child-extension", "longpolling" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "child-extension-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

