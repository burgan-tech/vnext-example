using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// AUTO-PAIR: fires when the execute-transfer task reported success.
public class ExecutionSucceededRule : IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        bool ok = false;
        try { ok = (bool?)(context.Instance?.Data?.transferResult?.success) ?? false; } catch { ok = false; }
        return Task.FromResult(ok);
    }
}
