using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Stamps the auth-code failure outcome on the issuer's errorBoundary transition.</summary>
public class SetAuthCodeFailedResultMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpAuthResult"] = "auth-code-failed";
        data["otpAuthResultDetail"] = "Authorization code service failure";
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
