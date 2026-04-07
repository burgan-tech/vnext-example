using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start Mapping - Minimal init for self-loop repro workflow
/// </summary>
public class StartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return new ScriptResponse
        {
            Key = "started",
            Data = new { 
                startedAt = DateTime.UtcNow, 
                repro = "self-loop-rule-invalidation",
                condition = 1
            }
        };
    }
}
