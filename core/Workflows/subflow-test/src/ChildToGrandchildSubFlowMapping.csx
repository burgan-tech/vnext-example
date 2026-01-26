using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Child to Grandchild SubFlow Mapping - Handles input/output for grandchild subflow state
/// This mapping prepares data for grandchild subflow and processes its result.
/// Manages the Level 2 to Level 3 transition in the subflow hierarchy.
/// </summary>
public class ChildToGrandchildSubFlowMapping : ScriptBase, ISubFlowMapping
{
    /// <summary>
    /// Prepares input data for the grandchild subflow
    /// </summary>
    public async Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ChildToGrandchildSubFlowMapping - Preparing grandchild subflow input data");
            
            // Prepare data to pass to grandchild workflow using Dictionary
            var testContext = new Dictionary<string, object>
            {
                ["testType"] = "deep-nested-view-extension-longpolling",
                ["expectViewData"] = true,
                ["expectExtensionData"] = true,
                ["nestingLevel"] = 3
            };
            
            var inputData = new Dictionary<string, object>
            {
                ["childInstanceId"] = context.Instance?.Id,
                ["childWorkflowId"] = context.Workflow?.Key ?? string.Empty,
                ["parentInstanceId"] = context.Instance?.Data?.parentInstanceId,
                ["parentWorkflowId"] = context.Instance?.Data?.parentWorkflowId,
                ["initiatedAt"] = DateTime.UtcNow,
                ["initiatedBy"] = "child-workflow",
                ["nestingLevel"] = 3,
                ["testContext"] = testContext
            };
            
            return new ScriptResponse
            {
                Key = context.Instance?.Key,
                Data = inputData,
                Tags = new[] { "subflow-test", "grandchild-subflow-input", "level-3" }
            };
        }
        catch (Exception ex)
        {
            LogError("ChildToGrandchildSubFlowMapping InputHandler - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "grandchild-subflow-input-error",
                Data = new Dictionary<string, object> { ["error"] = ex.Message }
            };
        }
    }

    /// <summary>
    /// Processes the result from the completed grandchild subflow
    /// </summary>
    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ChildToGrandchildSubFlowMapping - Processing grandchild subflow output data");
            
            // Get grandchild subflow result data
            var grandchildResult = context.Body;
            
            var grandchildResultData = new Dictionary<string, object>
            {
                ["grandchildInstanceData"] = grandchildResult,
                ["processed"] = true,
                ["viewTestResult"] = "success",
                ["extensionTestResult"] = "success",
                ["deepNestedTestResult"] = "success"
            };
            
            var childContinuation = new Dictionary<string, object>
            {
                ["canProceed"] = true,
                ["nextState"] = "child-after-grandchild"
            };
            
            var outputData = new Dictionary<string, object>
            {
                ["grandchildSubflowCompleted"] = true,
                ["completedAt"] = DateTime.UtcNow,
                ["grandchildResult"] = grandchildResultData,
                ["childContinuation"] = childContinuation,
                ["nestingLevel"] = 3
            };
            
            return new ScriptResponse
            {
                Key = "grandchild-subflow-output-processed",
                Data = outputData,
                Tags = new[] { "subflow-test", "grandchild-subflow-output", "completed", "level-3" }
            };
        }
        catch (Exception ex)
        {
            LogError("ChildToGrandchildSubFlowMapping OutputHandler - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "grandchild-subflow-output-error",
                Data = new Dictionary<string, object> { ["error"] = ex.Message }
            };
        }
    }
}
