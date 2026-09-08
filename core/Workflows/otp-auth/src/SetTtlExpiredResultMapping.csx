using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Stamps the timeout outcome on the paths the gate mappings do not cover.</summary>
public class SetTtlExpiredResultMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpAuthResult"] = "ttl-expired";
        data["otpAuthResultDetail"] = "OTP expired and no resend allowance remained";
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
