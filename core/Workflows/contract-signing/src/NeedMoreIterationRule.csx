using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// iterIndex < documentCount  → more documents remain to start
public class NeedMoreIterationRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var d = context.Instance.Data as IDictionary<string, object>;
        long iter = ToLong(d, "iterIndex");
        long count = ToLong(d, "documentCount");
        return Task.FromResult(iter < count);
    }

    private static long ToLong(IDictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v != null && long.TryParse(v.ToString(), out var n)) return n;
        return 0;
    }
}
