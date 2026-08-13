using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// iterIndex >= documentCount  → all documents started, stop iterating
public class NoMoreIterationRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var d = context.Instance.Data as IDictionary<string, object>;
        long iter = ToInt(d, "iterIndex");
        long count = ToInt(d, "documentCount");
        return Task.FromResult(iter >= count);
    }

    private static long ToInt(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v != null && long.TryParse(v.ToString(), out var n)) return n;
        return 0;
    }
}
