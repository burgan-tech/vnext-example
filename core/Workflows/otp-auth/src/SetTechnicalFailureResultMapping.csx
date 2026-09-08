using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Stamps the technical-failure outcome on errorBoundary transitions.</summary>
public class SetTechnicalFailureResultMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpAuthResult"] = "technical-failure";
        data["otpAuthResultDetail"] = "Upstream service failure";
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
