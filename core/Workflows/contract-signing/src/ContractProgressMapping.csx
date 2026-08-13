using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onExecution of the contract-progress updateData transition — the SINGLE progress channel
// from online-flow children to the contract: body {kind: "render"|"approval", documentId}.
//
// Why one updateData for both signals: updateData is accepted unconditionally (no Busy 409),
// always evaluates the current state's automatic transitions with the fresh data, and hands a
// satisfied gate to a real owner (parked-Busy takeover). Each state gates on its own rule:
// awaiting-renders counts rr_ stamps, approval-waiting counts ap_ stamps — a render callback
// landing in approval-waiting stamps rr_ and simply doesn't satisfy AllApprovedRule.
//
// A shared "count + 1" counter LOSES INCREMENTS under concurrency (each callback reads the
// same pre-write snapshot); instead each child stamps its OWN key (rr_/ap_{documentId}).
// Concurrent callbacks touch disjoint keys and repeats are naturally idempotent.
// Return ONLY the delta — a merged snapshot would clobber concurrent writes.
public class ContractProgressMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var inst = context.Instance.Data as IDictionary<string, object>;

        string kind = Str(inst, "kind");
        if (kind != "approval") kind = "render";
        var prefix = kind == "approval" ? "ap_" : "rr_";

        string docId = Str(inst, "documentId");
        if (string.IsNullOrWhiteSpace(docId))
        {
            LogInformation($"ContractProgressMapping: WARN no documentId in payload; stamping {prefix}unknown");
            docId = "unknown";
        }

        dynamic delta = new ExpandoObject();
        var target = (IDictionary<string, object>)delta;
        target[prefix + docId] = true;

        LogInformation(
            $"ContractProgressMapping: stamped {prefix}{docId} " +
            $"({kind}={CountPrefix(inst, prefix) + 1}/{Str(inst, "documentCount")})");
        return Task.FromResult(new ScriptResponse { Data = delta });
    }

    private static string Str(IDictionary<string, object> d, string k)
        => (d != null && d.TryGetValue(k, out var v) && v != null) ? v.ToString() : null;

    private static int CountPrefix(IDictionary<string, object> d, string prefix)
    {
        int n = 0;
        if (d == null) return 0;
        foreach (var kv in d)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                && kv.Value != null && bool.TryParse(kv.Value.ToString(), out var b) && b) n++;
        return n;
    }
}
