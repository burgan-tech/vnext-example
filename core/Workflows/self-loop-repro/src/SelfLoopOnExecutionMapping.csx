using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Self-loop auto transition onExecutionTask - target is same state ($self).
/// Reproduces case where this invalidates the rule so the state does not loop a second time.
/// </summary>
public class SelfLoopOnExecutionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return new ScriptResponse
        {
            Key = "self-loop-task-done",
            Data = new { 
                executedAt = DateTime.UtcNow, 
                note = "onExecutionTask target is self -> rule invalidated, no 2nd loop",
                condition = 0
            }
        };
    }
}
