using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Error Recovery Mapping - Handles error recovery logic
/// </summary>
public class ErrorRecoveryMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data;

        return new ScriptResponse
        {
            Key = "recovery-completed",
            Data = new
            {
                recoveryResult = new
                {
                    recoveryAttempted = true,
                    originalError = data?.lastError,
                    recoveryAction = "rollback",
                    recoveryAt = DateTime.UtcNow
                }
            },
            Tags = new[] { "error-boundary-test", "error-recovery" }
        };
    }
}

