using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Wrong code and the attempt allowance is now spent.</summary>
public class VerifyGateExceededRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(OtpAuthHelpers.GateIs(context.Instance?.Data, "verifyGate", "exceeded"));
    }
}
