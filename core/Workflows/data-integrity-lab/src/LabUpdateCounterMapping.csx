using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Counter task for the update-lab-progress updateData transition. Two modes:
///   - normal: merges current data and increments labUpdateCount by exactly one —
///     if any accepted (202) request is lost or double-applied, the final count will
///     not match the number of acceptances.
///   - noop probe (body: {"noop": true}): echoes the current data unchanged so the
///     write service's merged-hash dedup must swallow it — the pipeline completes but
///     NO new data version may appear.
/// </summary>
public class LabUpdateCounterMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Delta-only: return ONLY the keys this task owns. Echoing the whole snapshot would
        // overwrite concurrent writers' fresher values with stale ones.
        var data = context.Instance.Data as IDictionary<string, object>;

        var body = context.Body as IDictionary<string, object>;
        var isNoop = false;
        if (body != null && body.TryGetValue("noop", out var rawNoop) && rawNoop != null)
        {
            bool.TryParse(rawNoop.ToString(), out isNoop);
        }

        if (isNoop)
        {
            // Re-stamp an already-set key with the same value: merged content == head,
            // the write service dedups, no new version row.
            target["labStarted"] = true;
            LogInformation("LabUpdateCounterMapping: noop probe (expect dedup)");
            return Task.FromResult(new ScriptResponse { Data = result });
        }

        var current = 0;
        if (data != null && data.TryGetValue("labUpdateCount", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        target["labUpdateCount"] = current + 1;
        target["lastLabUpdateAt"] = DateTime.UtcNow.ToString("o");
        if (body != null && body.TryGetValue("updateNonce", out var nonce) && nonce != null)
        {
            target["lastLabNonce"] = nonce.ToString();
        }

        LogInformation($"LabUpdateCounterMapping: labUpdateCount {current} -> {current + 1}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
