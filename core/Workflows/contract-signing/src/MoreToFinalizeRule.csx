using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// finIndex < documentCount  → more child subprocesses remain to finalize
public class MoreToFinalizeRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var d = context.Instance.Data as IDictionary<string, object>;
        int fin = ToInt(d, "finIndex");
        int count = ToInt(d, "documentCount");
        return Task.FromResult(fin < count);
    }

    private static int ToInt(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var n)) return n;
        return 0;
    }
}
