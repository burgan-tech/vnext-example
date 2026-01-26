using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Grandchild Start Mapping - Initializes the grandchild workflow with data from child
/// This is the deepest level (level 3) in the subflow hierarchy.
/// </summary>
public class GrandchildStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("GrandchildStartMapping - Initializing grandchild workflow (Level 3)");
            
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "grandchild-start-success",
                Data = new
                {
                    grandchildWorkflowId = context.Workflow?.Key,
                    grandchildInstanceId = context.Instance?.Id,
                    childInstanceId = inputData?.childInstanceId,
                    childWorkflowId = inputData?.childWorkflowId,
                    parentInstanceId = inputData?.parentInstanceId,
                    parentWorkflowId = inputData?.parentWorkflowId,
                    nestingLevel = 3,
                    startedAt = DateTime.UtcNow,
                    status = "active",
                    message = "Grandchild workflow started successfully (Level 3)",
                    grandchildContext = new
                    {
                        isSubflow = true,
                        isDeepNested = true,
                        blocksParent = true,
                        viewTestEnabled = true,
                        extensionTestEnabled = true,
                        hierarchyPath = "Parent > Child > Grandchild"
                    }
                },
                Tags = new[] { "subflow-test", "grandchild-workflow", "started", "level-3" }
            };
        }
        catch (Exception ex)
        {
            LogError("GrandchildStartMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "grandchild-start-error",
                Data = new { error = ex.Message }
            };
        }
    }
}
