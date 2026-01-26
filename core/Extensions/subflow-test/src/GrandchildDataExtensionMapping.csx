using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Grandchild Data Extension Mapping - Provides additional data for grandchild workflow view
/// This extension is used to test longpolling functionality for deeply nested workflow views.
/// </summary>
public class GrandchildDataExtensionMapping : IMapping
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
                extensionType = "grandchild-data-extension",
                instanceId = context.Instance?.Id,
                workflowKey = context.Workflow?.Key,
                parentInstanceId = context.Instance?.Data?.parentInstanceId,
                childInstanceId = context.Instance?.Data?.childInstanceId,
                nestingLevel = 3,
                requestedAt = DateTime.UtcNow
            };

            httpTask.SetBody(requestBody);

            // Set headers
            var headers = new Dictionary<string, string?>
            {
                ["Content-Type"] = "application/json",
                ["X-Extension-Type"] = "grandchild-data",
                ["X-Nesting-Level"] = "3",
                ["X-Request-Id"] = Guid.NewGuid().ToString()
            };
            httpTask.SetHeaders(headers);

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "grandchild-extension-input-error",
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
                Key = "grandchild-extension-success",
                Data = new
                {
                    grandchildExtension = new
                    {
                        extensionName = "grandchild-data-extension",
                        source = "grandchild-workflow",
                        nestingLevel = 3,
                        loadedAt = DateTime.UtcNow,
                        data = new
                        {
                            grandchildConfig = new
                            {
                                isSubflow = true,
                                isDeepNested = true,
                                blocksParent = true,
                                completionRequired = true
                            },
                            grandchildMetadata = new
                            {
                                workflowType = "deep-subflow",
                                parentWorkflow = "subflow-view-test-child",
                                grandparentWorkflow = "subflow-view-test-parent",
                                extensionVersion = "1.0.0"
                            },
                            hierarchyPath = new
                            {
                                level1 = "subflow-view-test-parent",
                                level2 = "subflow-view-test-child",
                                level3 = "subflow-view-test-grandchild"
                            }
                        }
                    }
                },
                Tags = new[] { "subflow-test", "grandchild-extension", "longpolling", "deep-nested" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "grandchild-extension-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
