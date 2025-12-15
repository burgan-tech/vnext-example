using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// GetConfigValue Test Mapping - Demonstrates configuration value retrieval
/// Test case: Verify GetConfigValue functionality and logging
/// </summary>
public class GetConfigValueMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            LogInformation("GetConfigValue Test - Starting configuration retrieval test");

            // Retrieve API base URL from configuration using GetConfigValue
            var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl");
            
            LogInformation("GetConfigValue Test - Successfully retrieved ApiBaseUrl: {0}", 
                args: new object?[] { apiBaseUrl ?? "null" });

            return Task.FromResult(new ScriptResponse
            {
                Data = new
                {
                    configRetrieved = !string.IsNullOrEmpty(apiBaseUrl),
                    configKeyName = "ApiBaseUrl",
                    configValue = apiBaseUrl,
                    retrievedAt = DateTime.UtcNow
                }
            });
        }
        catch (Exception ex)
        {
            LogError("GetConfigValue Test - Failed to retrieve config: {0}", 
                args: new object?[] { ex.Message });
            
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-config-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("GetConfigValue Test - Processing output");

            var inputData = context.Instance?.Data;

            return Task.FromResult(new ScriptResponse
            {
                Key = "get-config-success",
                Data = new
                {
                    getConfigResult = new
                    {
                        success = true,
                        testName = "GetConfigValue",
                        description = "Retrieved ApiBaseUrl from configuration",
                        taskType = "ScriptTask"
                    }
                },
                Tags = new[] { "task-test", "get-config-value", "success" }
            });
        }
        catch (Exception ex)
        {
            LogError("GetConfigValue Test - Output handler exception: {0}", 
                args: new object?[] { ex.Message });
            
            return Task.FromResult(new ScriptResponse
            {
                Key = "get-config-exception",
                Data = new
                {
                    getConfigResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "ScriptTask"
                    }
                }
            });
        }
    }
}

