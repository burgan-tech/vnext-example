using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>No resend allowance left; end the flow as timed out.</summary>
public class ResendGateExhaustedRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(OtpAuthHelpers.GateIs(context.Instance?.Data, "resendGate", "exhausted"));
    }
}
