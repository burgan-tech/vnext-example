using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Parent Start Mapping - Initializes the parent workflow
/// </summary>
public class ParentStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ParentStartMapping - Initializing parent workflow");
            
            var inputData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "parent-start-success",
                Data = new
                {
                    parentWorkflowId = context.Workflow?.Key,
                    parentInstanceId = context.Instance?.Id,
                    startedAt = DateTime.UtcNow,
                    status = "started",
                    message = "Parent workflow started successfully",
                    testContext = new
                    {
                        testType = "subflow-view-extension-test",
                        viewTestEnabled = true,
                        extensionTestEnabled = true,
                        longpollingTestEnabled = true
                    },
                    parentContext = new
                    {
                        isMainWorkflow = true,
                        willCallSubflow = true,
                        hasOwnViewAndExtension = true
                    }
                },
                Tags = new[] { "subflow-test", "parent-workflow", "started" }
            };
        }
        catch (Exception ex)
        {
            LogError("ParentStartMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "parent-start-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

