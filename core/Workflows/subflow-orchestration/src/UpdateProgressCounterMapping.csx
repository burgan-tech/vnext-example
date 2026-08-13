using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Counter task for the parent's updateData transition (update-parent-progress).
/// Merges the current instance data and increments updateCount by exactly one.
/// Concurrent-consistency probe: if any increment is lost or duplicated, the final
/// updateCount will not match the number of accepted (202) updateData requests.
/// </summary>
public class UpdateProgressCounterMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null)
        {
            foreach (var kv in inst)
            {
                target[kv.Key] = kv.Value;
            }
        }

        var current = 0;
        if (inst != null && inst.TryGetValue("updateCount", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        target["updateCount"] = current + 1;
        target["lastUpdateAt"] = DateTime.UtcNow.ToString("o");

        // Carry the caller's nonce (if any) so each accepted request leaves a trace.
        var body = context.Body as IDictionary<string, object>;
        if (body != null && body.TryGetValue("updateNonce", out var nonce) && nonce != null)
        {
            target["lastUpdateNonce"] = nonce.ToString();
        }

        LogInformation($"UpdateProgressCounterMapping: updateCount {current} -> {current + 1}");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
