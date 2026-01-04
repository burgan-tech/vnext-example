using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Child Complete Mapping - Marks the child workflow as completed
/// This data will be returned to the parent workflow
/// </summary>
public class ChildCompleteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ChildCompleteMapping - Completing child workflow");
            
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "child-complete-success",
                Data = new
                {
                    completedAt = DateTime.UtcNow,
                    status = "completed",
                    result = new
                    {
                        childWorkflowId = context.Workflow?.Key,
                        childInstanceId = context.Instance?.Id,
                        success = true,
                        message = "Child workflow completed successfully",
                        dataForParent = new
                        {
                            childResult = "processed",
                            processingTime = 100,
                            viewTestPassed = true,
                            extensionTestPassed = true
                        }
                    }
                },
                Tags = new[] { "subflow-test", "child-workflow", "completed" }
            };
        }
        catch (Exception ex)
        {
            LogError("ChildCompleteMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "child-complete-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

