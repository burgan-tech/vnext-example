using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// AUTO-PAIR: complement of ExecutionSucceededRule. Fires when the execute-transfer task did not succeed.
public class ExecutionFailedRule : IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        bool ok = false;
        try { ok = (bool?)(context.Instance?.Data?.transferResult?.success) ?? false; } catch { ok = false; }
        return Task.FromResult(!ok);
    }
}
