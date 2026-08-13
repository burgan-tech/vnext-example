using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// Every document reported render-ready.
// Counts per-document stamps ("rr_{documentId}") instead of a shared counter, because
// concurrent fan-in callbacks lose increments on a shared read-modify-write key.
public class AllRenderedRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var d = context.Instance.Data as IDictionary<string, object>;
        int stamped = 0, total = 0;
        if (d != null)
        {
            foreach (var kv in d)
                if (kv.Key.StartsWith("rr_", StringComparison.Ordinal)
                    && kv.Value != null && bool.TryParse(kv.Value.ToString(), out var b) && b) stamped++;
            if (d.TryGetValue("documentCount", out var c) && c != null) int.TryParse(c.ToString(), out total);
        }
        return Task.FromResult(total > 0 && stamped >= total);
    }
}
