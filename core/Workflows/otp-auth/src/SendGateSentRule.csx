using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>OTP service answered SendOtpSuccess.</summary>
public class SendGateSentRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(OtpAuthHelpers.GateIs(context.Instance?.Data, "sendGate", "sent"));
    }
}
