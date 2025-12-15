using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Script Task Mapping - Executes custom business logic
/// Test case: Verify script execution and data transformation
/// </summary>
public class ScriptTaskMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            // Script tasks don't require specific task configuration
            // They are used for data transformation and business logic
            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "script-task-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            // Perform data transformation
            var inputData = context.Instance?.Data;
            
            // Calculate some values
            var calculatedValues = new
            {
                originalTestId = inputData?.testId,
                processedAt = DateTime.UtcNow,
                calculatedValue = new Random().Next(1000, 9999),
                isProcessed = true,
                processingDuration = DateTime.UtcNow.Subtract(DateTime.UtcNow.AddMilliseconds(-100)).TotalMilliseconds
            };

            // Transform and aggregate data
            var transformedData = new
            {
                scriptTaskResult = new
                {
                    success = true,
                    originalData = new
                    {
                        testId = inputData?.testId,
                        workflowId = context.Workflow.Key,
                        instanceId = context.Instance?.Id
                    },
                    transformed = calculatedValues,
                    metadata = new
                    {
                        taskType = "ScriptTask",
                        executedAt = DateTime.UtcNow,
                        scriptVersion = "1.0.0"
                    }
                }
            };

            return new ScriptResponse
            {
                Key = "script-task-success",
                Data = transformedData,
                Tags = new[] { "task-test", "script-task", "success", "data-transformation" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "script-task-exception",
                Data = new
                {
                    scriptTaskResult = new
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

