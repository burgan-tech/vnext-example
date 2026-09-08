using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Wrong code but attempts remain; return to the entry state.</summary>
public class VerifyGateRetryRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(OtpAuthHelpers.GateIs(context.Instance?.Data, "verifyGate", "retry"));
    }
}
