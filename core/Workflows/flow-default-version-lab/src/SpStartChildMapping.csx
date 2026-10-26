using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of sp-initial: start default-version-lab-sp-child through a LOCAL SubProcess task (type 14),
// authored with the FLOATING version "1" — the registry pointer on the child decides which version runs,
// and the parent's correlation must record the version the child actually started on.
public class SpStartChildMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask;
        if (sub == null) throw new InvalidOperationException("Task must be a SubProcessTask");

        sub.SetDomain("core");
        sub.SetFlow("default-version-lab-sp-child");
        sub.SetVersion("1");
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
