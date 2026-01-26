using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Grandchild Complete Mapping - Marks the grandchild workflow as completed
/// This data will be returned to the child workflow (Level 2)
/// </summary>
public class GrandchildCompleteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("GrandchildCompleteMapping - Completing grandchild workflow (Level 3)");
            
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "grandchild-complete-success",
                Data = new
                {
                    completedAt = DateTime.UtcNow,
                    status = "completed",
                    nestingLevel = 3,
                    result = new
                    {
                        grandchildWorkflowId = context.Workflow?.Key,
                        grandchildInstanceId = context.Instance?.Id,
                        success = true,
                        message = "Grandchild workflow completed successfully (Level 3)",
                        dataForChild = new
                        {
                            grandchildResult = "processed",
                            processingTime = 100,
                            viewTestPassed = true,
                            extensionTestPassed = true,
                            deepNestedTestPassed = true
                        }
                    }
                },
                Tags = new[] { "subflow-test", "grandchild-workflow", "completed", "level-3" }
            };
        }
        catch (Exception ex)
        {
            LogError("GrandchildCompleteMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "grandchild-complete-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
