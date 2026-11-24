using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// faulted-recovery-lab: the input mapping of fr-task's onExecution script task. It throws when the request body
/// says failTask=true — a fault AFTER the transition record was saved (the step that existed before vnext#1252).
/// </summary>
public class TaskOnDemandMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var body = context.Body;
        if (body != null && HasProperty(body, "failTask") && body.failTask?.ToString() is "True" or "true")
            throw new InvalidOperationException("faulted-recovery-lab: the request asked the task to fail");

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) => Task.FromResult(new ScriptResponse());
}
