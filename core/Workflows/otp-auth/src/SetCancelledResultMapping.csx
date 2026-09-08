using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>Stamps the cancelled outcome on the workflow-level cancel transition.</summary>
public class SetCancelledResultMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpAuthResult"] = "cancelled";
        data["otpAuthResultDetail"] = "Cancelled by the caller";
        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
