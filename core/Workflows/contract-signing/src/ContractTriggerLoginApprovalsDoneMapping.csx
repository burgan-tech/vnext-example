using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of approval-done: every child reported approval-received, so notify the parent
// exactly once via DirectTrigger on the approvalsDoneTransition ref (login-approvals-done),
// targeting the parent instance id (contractId) passed at start.
// Per-document approvals are NOT forwarded upward — only this single aggregate signal.
public class ContractTriggerLoginApprovalsDoneMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        var ad = Ref(data, "approvalsDoneTransition");
        trig.SetDomain(ad.domain ?? "core");
        trig.SetFlow(ad.flow ?? "login-flow");
        trig.SetTransitionName(ad.key ?? "login-approvals-done");
        trig.SetInstance(Str(data, "contractId"));
        trig.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("ContractTriggerLoginApprovalsDoneMapping: parent login-approvals-done triggered");
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    private static (string domain, string flow, string key) Ref(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v is IDictionary<string, object> r)
            return (Get(r, "domain"), Get(r, "flow"), Get(r, "key"));
        return (null, null, null);
    }
    private static string Get(IDictionary<string, object> d, string k)
        => (d != null && d.TryGetValue(k, out var v) && v != null) ? v.ToString() : null;
    private static string Str(IDictionary<string, object> d, string k)
        => (d != null && d.TryGetValue(k, out var v) && v != null) ? v.ToString() : null;
}
