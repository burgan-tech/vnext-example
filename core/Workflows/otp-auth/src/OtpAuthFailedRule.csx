using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Harness fallback branch: everything the SubFlow can end on that is not "success".
/// Paired with OtpAuthSucceededRule so the two are total and mutually exclusive.
/// </summary>
public class OtpAuthFailedRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        return Task.FromResult(!OtpAuthHelpers.GateIs(context.Instance?.Data, "otpAuthResult", "success"));
    }
}
