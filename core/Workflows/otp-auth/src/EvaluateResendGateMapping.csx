using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Decides, after a TTL expiry, whether the user still gets a manual resend or the flow ends.
/// Keeping the comparison here means the two exit rules stay pure equality checks.
/// </summary>
public class EvaluateResendGateMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var used = OtpAuthHelpers.Int(data, "resendUsed", 0);
        var limit = OtpAuthHelpers.Int(data, "resendLimit", 3);

        data["resendGate"] = used < limit ? "allowed" : "exhausted";
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
