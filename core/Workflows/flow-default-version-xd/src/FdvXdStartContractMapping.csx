using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onEntry of sp-initial: start partner/fdv-xd-contract through a SubProcess task (type 14) over Dapr,
/// authored with the FLOATING version "1" — the partner's default pointer decides the version.
/// </summary>
public class FdvXdStartContractMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask ?? throw new InvalidOperationException("Task must be a SubProcessTask");

        sub.SetDomain("partner");
        sub.SetFlow("fdv-xd-contract");
        sub.SetVersion("1");
        sub.SetUseDapr(true);
        sub.SetKey($"fdv-xd-sp-{context.Instance?.Id}");
        sub.SetSync(false);
        sub.SetBody(new { parentInstanceId = context.Instance?.Id, parentDomain = "core" });

        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
