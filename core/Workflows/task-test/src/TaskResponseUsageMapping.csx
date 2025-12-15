using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// TaskResponse Usage Test Mapping - Demonstrates accessing previous task responses
/// Test case: Verify TaskResponse dictionary usage in ScriptContext after HTTP tasks
/// Note: TaskResponse keys are the task's "key" value converted to camelCase
/// Example: "test-http-task" becomes "testHttpTask"
/// </summary>
public class TaskResponseUsageMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            LogInformation("TaskResponse Usage Test - Starting TaskResponse access test");

            // Access TaskResponse dictionary from ScriptContext
            // This contains results from previously completed tasks in the workflow
            var taskResponseDict = context.TaskResponse;

            if (taskResponseDict != null && taskResponseDict.Count > 0)
            {
                LogInformation("TaskResponse Usage Test - Found {0} task response(s) in context", 
                    args: new object?[] { taskResponseDict.Count });

                // Log all available task response keys
                foreach (var key in taskResponseDict.Keys)
                {
                    LogDebug("TaskResponse Usage Test - Available task response key: {0}", 
                        args: new object?[] { key });
                }

                // Example: Access specific task response if it exists
                // TaskResponse key is the task's "key" value converted to camelCase
                // e.g., "test-http-task" becomes "testHttpTask"
                if (taskResponseDict.ContainsKey("testHttpTask"))
                {
                    var httpTaskResult = taskResponseDict["testHttpTask"];
                    LogInformation("TaskResponse Usage Test - HTTP task response found: isSuccess={0}, statusCode={1}", 
                        args: new object?[] { httpTaskResult?.isSuccess, httpTaskResult?.statusCode });
                    
                    // Access response data
                    var responseData = httpTaskResult?.data;
                    if (responseData != null)
                    {
                        LogDebug("TaskResponse Usage Test - HTTP response data available");
                    }
                }
            }
            else
            {
                LogWarning("TaskResponse Usage Test - No previous task responses found in context");
            }

            return Task.FromResult(new ScriptResponse
            {
                Data = new
                {
                    taskResponseCount = taskResponseDict?.Count ?? 0,
                    taskResponseKeys = taskResponseDict != null 
                        ? string.Join(", ", taskResponseDict.Keys) 
                        : "none",
                    testedAt = DateTime.UtcNow
                }
            });
        }
        catch (Exception ex)
        {
            LogError("TaskResponse Usage Test - Failed to access TaskResponse: {0}", 
                args: new object?[] { ex.Message });
            
            return Task.FromResult(new ScriptResponse
            {
                Key = "task-response-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("TaskResponse Usage Test - Processing output");

            // Demonstrate accessing TaskResponse in OutputHandler as well
            var taskResponseDict = context.TaskResponse;
            
            // Build a summary of available task responses
            var taskResponseSummary = new System.Collections.Generic.List<object>();
            
            if (taskResponseDict != null)
            {
                foreach (var kvp in taskResponseDict)
                {
                    taskResponseSummary.Add(new
                    {
                        taskKey = kvp.Key,
                        hasData = kvp.Value != null,
                        isSuccess = kvp.Value?.isSuccess,
                        statusCode = kvp.Value?.statusCode,
                        taskType = kvp.Value?.taskType
                    });
                }
            }

            LogInformation("TaskResponse Usage Test - Processed {0} task response(s)", 
                args: new object?[] { taskResponseSummary.Count });

            return Task.FromResult(new ScriptResponse
            {
                Key = "task-response-success",
                Data = new
                {
                    taskResponseResult = new
                    {
                        success = true,
                        testName = "TaskResponse Usage",
                        description = "Demonstrated context.TaskResponse dictionary access",
                        totalTaskResponses = taskResponseDict?.Count ?? 0,
                        taskResponseSummary = taskResponseSummary,
                        completedAt = DateTime.UtcNow,
                        taskType = "ScriptTask"
                    }
                },
                Tags = new[] { "task-test", "task-response", "context-access", "success" }
            });
        }
        catch (Exception ex)
        {
            LogError("TaskResponse Usage Test - Output handler exception: {0}", 
                args: new object?[] { ex.Message });
            
            return Task.FromResult(new ScriptResponse
            {
                Key = "task-response-exception",
                Data = new
                {
                    taskResponseResult = new
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

