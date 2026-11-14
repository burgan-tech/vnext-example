using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of st-initial: start the child through a Start task (type 11). The version comes ONLY from
// the task's config ("version": "1.0.0") — this mapping must not call SetVersion, otherwise it would
// hide whether the config value reaches the start. Only the key is set, so the test can find the child.
public class StartChildStartTaskMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var start = task as StartTask;
        if (start == null) throw new InvalidOperationException("Task must be a StartTask");

        start.SetKey(context.Instance.Id + "-start");
        start.SetBody(new { });
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
