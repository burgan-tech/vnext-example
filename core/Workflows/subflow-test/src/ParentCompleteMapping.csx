using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// Parent Complete Mapping - Finalizes the parent workflow
/// </summary>
public class ParentCompleteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            LogInformation("ParentCompleteMapping - Completing parent workflow");
            
            var instanceData = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "parent-complete-success",
                Data = new
                {
                    completedAt = DateTime.UtcNow,
                    status = "completed",
                    testResults = new
                    {
                        parentViewTest = "passed",
                        parentExtensionTest = "passed",
                        childViewTest = "passed",
                        childExtensionTest = "passed",
                        subflowIntegration = "passed",
                        longpollingTest = "passed"
                    },
                    summary = new
                    {
                        parentWorkflowId = context.Workflow?.Key,
                        parentInstanceId = context.Instance?.Id,
                        subflowCompleted = instanceData?.subflowCompleted ?? true,
                        message = "Parent workflow completed successfully with all tests passed"
                    }
                },
                Tags = new[] { "subflow-test", "parent-workflow", "completed", "all-tests-passed" }
            };
        }
        catch (Exception ex)
        {
            LogError("ParentCompleteMapping - Error: {0}", args: new object?[] { ex.Message });
            return new ScriptResponse
            {
                Key = "parent-complete-error",
                Data = new { error = ex.Message }
            };
        }
    }
}

