using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onEntry of pre-approved: DirectTrigger the parent contract-flow's contract-progress
// updateData ({kind: "approval"}), using the approvalReceived ref + contractInstanceId passed
// at start. updateData is accepted unconditionally: rapid-fire user approvals can no longer
// hit a Busy 409 window.
// errorBoundary Retry absorbs instance-lock contention on the parent.
public class OnlineNotifyApprovalMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trig = task as DirectTriggerTask;
        if (trig == null) throw new InvalidOperationException("Task must be a DirectTriggerTask");

        var data = context.Instance.Data as IDictionary<string, object>;
        var ar = Ref(data, "approvalReceived");
        trig.SetDomain(ar.domain ?? "core");
        trig.SetFlow(ar.flow ?? "contract-flow");
        trig.SetTransitionName(ar.key ?? "contract-progress");
        trig.SetInstance(Str(data, "contractInstanceId"));
        // The parent stamps rr_/ap_{documentId} rather than incrementing a shared counter,
        // so it needs to know WHICH document this callback is for.
        trig.SetBody(new { kind = "approval", documentId = Str(data, "documentId") });
        trig.SetSync(false);
        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null) foreach (var kv in inst) target[kv.Key] = kv.Value;
        target["approved"] = true;
        LogInformation("OnlineNotifyApprovalMapping: parent contract-progress(approval) triggered");
        return Task.FromResult(new ScriptResponse { Data = merged });
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
