using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>SIM blocked on a Kurumsal caller with the latch still open: retry over FAST.</summary>
public class SendGateEscalateFastRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(OtpAuthHelpers.GateIs(context.Instance?.Data, "sendGate", "escalate-fast"));
    }
}
