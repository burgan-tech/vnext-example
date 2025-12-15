using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// GetSecretAsync Test Mapping - Demonstrates secret retrieval from Dapr secret store
/// Test case: Verify GetSecretAsync functionality and logging
/// </summary>
public class GetSecretAsyncMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            LogInformation("GetSecretAsync Test - Starting secret retrieval test");

            // Retrieve API secret from Dapr secret store using GetSecretAsync
            var apiSecret = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
            
            LogInformation("GetSecretAsync Test - Successfully retrieved ApiSecret");
            LogDebug("GetSecretAsync Test - Secret value length: {0}", 
                args: new object?[] { apiSecret?.Length ?? 0 });

            // Store the result for output handler
            return new ScriptResponse
            {
                Data = new
                {
                    secretRetrieved = !string.IsNullOrEmpty(apiSecret),
                    secretKeyName = "ApiSecret",
                    retrievedAt = DateTime.UtcNow
                }
            };
        }
        catch (Exception ex)
        {
            LogError("GetSecretAsync Test - Failed to retrieve secret: {0}", 
                args: new object?[] { ex.Message });
            
            return new ScriptResponse
            {
                Key = "get-secret-input-error",
                Data = new { error = ex.Message }
            };
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("GetSecretAsync Test - Processing output");

            return new ScriptResponse
            {
                Key = "get-secret-success",
                Data = new
                {
                    getSecretResult = new
                    {
                        success = true,
                        testName = "GetSecretAsync",
                        description = "Retrieved ApiSecret from Dapr secret store",
                        completedAt = DateTime.UtcNow,
                        taskType = "ScriptTask"
                    }
                },
                Tags = new[] { "task-test", "get-secret-async", "success" }
            };
        }
        catch (Exception ex)
        {
            LogError("GetSecretAsync Test - Output handler exception: {0}", 
                args: new object?[] { ex.Message });
            
            return new ScriptResponse
            {
                Key = "get-secret-exception",
                Data = new
                {
                    getSecretResult = new
                    {
                        success = false,
                        error = ex.Message,
                        taskType = "ScriptTask"
                    }
                }
            };
        }
    }
}

