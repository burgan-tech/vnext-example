using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Starts ssp-child through a SYNC SubProcess task and records what the start response says about
/// the child: its id, its own state (currentState) and its effective state. The child auto-chains
/// out of its initial state, so the recorded state proves the response reports the REST point,
/// not the state the child was born in (vnext#1182).
/// </summary>
public class SspStartChildMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask;
        if (sub == null) throw new InvalidOperationException("Task must be a SubProcessTask");

        sub.SetDomain("core");
        sub.SetFlow("ssp-child");
        sub.SetVersion("1.0.0");
        sub.SetBody(new { });
        sub.SetKey(context.Instance.Id.ToString());
        sub.SetSync(true);
        return Task.FromResult(new ScriptResponse());
    }

    // A same-domain SubProcess start nests the start output under data.value; a cross-domain one
    // passes the remote body through flat (data.*). Read both, same as the id readers elsewhere.
    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        object id = null, current = null, effective = null;
        try { id = context.Body?.data?.value?.id; current = context.Body?.data?.value?.currentState; effective = context.Body?.data?.value?.effectiveState; } catch { }
        if (id == null)
        {
            try { id = context.Body?.data?.id; current = context.Body?.data?.currentState; effective = context.Body?.data?.effectiveState; } catch { }
        }
        target["childId"] = id?.ToString();
        target["childCurrentState"] = current?.ToString();
        target["childEffectiveState"] = effective?.ToString();
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
