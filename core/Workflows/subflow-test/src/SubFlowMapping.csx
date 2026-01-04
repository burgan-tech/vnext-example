using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// SubFlow Mapping - Handles input/output for subflow state
/// This mapping prepares data for subflow and processes its result
/// </summary>
public class SubFlowMapping : ScriptBase, ISubFlowMapping
{
    /// <summary>
    /// Prepares input data for the subflow
    /// </summary>
    public async Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("SubFlowMapping - Preparing subflow input data");
            
            // Prepare data to pass to child workflow using Dictionary
            var testContext = new Dictionary<string, object>
            {
                ["testType"] = "view-extension-longpolling",
                ["expectViewData"] = true,
                ["expectExtensionData"] = true
            };
            
            var inputData = new Dictionary<string, object>
            {
                ["parentInstanceId"] = context.Instance?.Id,
                ["parentWorkflowId"] = context.Workflow?.Key ?? string.Empty,
                ["initiatedAt"] = DateTime.UtcNow,
                ["initiatedBy"] = "parent-workflow",
                ["testContext"] = testContext
            };
            
            return new ScriptResponse
            {
                Key = context.Instance?.Key,
                Data = inputData,
                Tags = new[] { "subflow-test", "subflow-input" }
            };
        }
        catch (Exception ex)
        {
            LogError("SubFlowMapping InputHandler - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "subflow-input-error",
                Data = new Dictionary<string, object> { ["error"] = ex.Message }
            };
        }
    }

    /// <summary>
    /// Processes the result from the completed subflow
    /// </summary>
    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("SubFlowMapping - Processing subflow output data");
            
            // Get subflow result data
            var subflowResult = context.Body;
            
            var subflowResultData = new Dictionary<string, object>
            {
                ["childInstanceData"] = subflowResult,
                ["processed"] = true,
                ["viewTestResult"] = "success",
                ["extensionTestResult"] = "success"
            };
            
            var parentContinuation = new Dictionary<string, object>
            {
                ["canProceed"] = true,
                ["nextState"] = "parent-after-subflow"
            };
            
            var outputData = new Dictionary<string, object>
            {
                ["subflowCompleted"] = true,
                ["completedAt"] = DateTime.UtcNow,
                ["subflowResult"] = subflowResultData,
                ["parentContinuation"] = parentContinuation
            };
            
            return new ScriptResponse
            {
                Key = "subflow-output-processed",
                Data = outputData,
                Tags = new[] { "subflow-test", "subflow-output", "completed" }
            };
        }
        catch (Exception ex)
        {
            LogError("SubFlowMapping OutputHandler - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "subflow-output-error",
                Data = new Dictionary<string, object> { ["error"] = ex.Message }
            };
        }
    }
}

