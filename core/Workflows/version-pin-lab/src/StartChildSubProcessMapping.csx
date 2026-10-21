using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of sp-initial: start the child through a SubProcess task (type 14), authored with the
// PARTIAL version "1.0" — the runtime must record the version it resolved (1.0.3), not "1.0".
public class StartChildSubProcessMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask;
        if (sub == null) throw new InvalidOperationException("Task must be a SubProcessTask");

        sub.SetDomain("core");
        sub.SetFlow("version-pin-lab-child");
        sub.SetVersion("1.0");
        sub.SetBody(new { });
        sub.SetKey(context.Instance.Id.ToString());
        sub.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
