using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Task 2 mapping for multi-task function test.
/// Fetches current workflow instance data via GetInstanceDataTask.
/// </summary>
public class LookupPaymentTypes : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Key = "self-loop-task-done",
            Data = new { 
                executedAt = DateTime.UtcNow, 
                note = "onExecutionTask target is self -> rule invalidated, no 2nd loop",
                condition = 0
            }
        });
    }
}
