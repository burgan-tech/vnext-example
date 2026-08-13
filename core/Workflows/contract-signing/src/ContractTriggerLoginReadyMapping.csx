using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

// onEntry of render-ready-done: DirectTrigger the parent's readyTransition (login-ready),
// using the transition ref + parent instance id (contractId) passed at start.
public class ContractTriggerLoginReadyMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        var rt = Ref(data, "readyTransition");
        trig.SetDomain(rt.domain ?? "core");
        trig.SetFlow(rt.flow ?? "login-flow");
        trig.SetTransitionName(rt.key ?? "login-ready");
        trig.SetInstance(Str(data, "contractId"));
        trig.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("ContractTriggerLoginReadyMapping: parent login-ready triggered");
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
