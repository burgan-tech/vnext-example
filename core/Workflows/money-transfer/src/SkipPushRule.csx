using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// AUTO-PAIR B: complement of RequirePushRule. Fires when the targetIban has been used before
// (isFirstTransfer == false) -> skip push, go straight to execution.
public class SkipPushRule : IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        bool isFirst = false;
        try { isFirst = (bool?)(context.Instance?.Data?.isFirstTransfer) ?? false; } catch { isFirst = false; }
        return Task.FromResult(!isFirst);
    }
}
