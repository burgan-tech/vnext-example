using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// contractCompleted == true  → contract-flow reported that it finalized every document
// and is closing itself, so login may continue.
public class ContractCompletedRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var d = context.Instance.Data as IDictionary<string, object>;
        if (d != null && d.TryGetValue("contractCompleted", out var v) && v != null
            && bool.TryParse(v.ToString(), out var done)) return Task.FromResult(done);
        return Task.FromResult(false);
    }
}
