using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of contract-completed: DirectTrigger the parent's completedTransition (login-completed),
// using the transition ref + parent instance id (contractId) passed at start.
// The parent target is login-flow's updateData transition (contract-status): contract-flow
// hands up a contractCompleted flag instead of driving the parent's state directly, and
// login-flow decides for itself when to move on.
public class ContractTriggerLoginCompletedMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        var ct = Ref(data, "completedTransition");
        trig.SetDomain(ct.domain ?? "core");
        trig.SetFlow(ct.flow ?? "login-flow");
        trig.SetTransitionName(ct.key ?? "contract-status");
        trig.SetInstance(Str(data, "contractId"));
        trig.SetBody(new { contractCompleted = true });
        trig.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("ContractTriggerLoginCompletedMapping: contract-status updateData sent (contractCompleted=true)");
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