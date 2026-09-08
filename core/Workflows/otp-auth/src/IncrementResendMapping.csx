using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Consumes one manual resend. Deliberately does NOT touch attemptUsed: the wrong-entry allowance is
/// per instance, so a resend must not hand the user a fresh set of guesses.
/// </summary>
public class IncrementResendMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["resendUsed"] = OtpAuthHelpers.Int(data, "resendUsed", 0) + 1;
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
