using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Child Start Mapping - Initializes the child workflow with data from parent
/// </summary>
public class ChildStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ChildStartMapping - Initializing child workflow");
            
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "child-start-success",
                Data = new
                {
                    childWorkflowId = context.Workflow?.Key,
                    childInstanceId = context.Instance?.Id,
                    parentInstanceId = inputData?.parentInstanceId,
                    parentWorkflowId = inputData?.parentWorkflowId,
                    startedAt = DateTime.UtcNow,
                    status = "active",
                    message = "Child workflow started successfully",
                    childContext = new
                    {
                        isSubflow = true,
                        blocksParent = true,
                        viewTestEnabled = true,
                        extensionTestEnabled = true
                    }
                },
                Tags = new[] { "subflow-test", "child-workflow", "started" }
            };
        }
        catch (Exception ex)
        {
            LogError("ChildStartMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "child-start-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

