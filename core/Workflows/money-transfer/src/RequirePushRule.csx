using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// AUTO-PAIR A: fires when this is the first transfer to the targetIban -> require mobile push approval.
public class RequirePushRule : IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        bool isFirst = false;
        try { isFirst = (bool?)(context.Instance?.Data?.isFirstTransfer) ?? false; } catch { isFirst = false; }
        return Task.FromResult(isFirst);
    }
}
