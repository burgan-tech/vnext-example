using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Rollback Mapping - Handles rollback action for critical errors
/// </summary>
public class RollbackMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var data = context.Instance?.Data;
            
            return new ScriptResponse
            {
                Key = "rollback-completed",
                Data = new
                {
                    rollbackResult = new
                    {
                        rollbackExecuted = true,
                        originalError = data?.lastError,
                        rollbackAction = "revert-changes",
                        rollbackAt = DateTime.UtcNow,
                        note = "Rollback action executed due to ErrorBoundary configuration"
                    }
                },
                Tags = new[] { "error-boundary-test", "rollback" }
            };
        }
        catch (Exception ex)
        {
            return new ScriptResponse
            {
                Key = "rollback-exception",
                Data = new { error = ex.Message }
            };
        }
    }
}

