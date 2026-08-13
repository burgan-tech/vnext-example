using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// AUTO-PAIR: complement of SmsSucceededRule. Fires when the VIP SMS SOAP task did not succeed.
public class SmsFailedRule : IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        bool ok = false;
        try { ok = (bool?)(context.Instance?.Data?.smsResult?.success) ?? false; } catch { ok = false; }
        return Task.FromResult(!ok);
    }
}
