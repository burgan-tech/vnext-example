using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// finIndex >= documentCount  → every child subprocess has been finalized
public class AllFinalizedRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(context.Instance.Data.finIndex >= context.Instance.Data.documentCount);
    }
}
