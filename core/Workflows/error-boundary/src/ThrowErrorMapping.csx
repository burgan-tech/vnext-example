using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class ThrowErrorMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        throw new Exception("Intentional test error for error boundary");
    }
}
