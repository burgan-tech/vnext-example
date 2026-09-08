using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Runs on the errorBoundary path when the runtime raises 404/400 instead of handing the response to
/// GetSimpleProfileMapping. Recomputes profileGate with the same scope-aware rule so both routes
/// converge on identical behaviour.
/// </summary>
public class ProfileNotFoundFallbackMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var isKurumsal = string.Equals(OtpAuthHelpers.Str(data, "scopeGroup", "Bireysel"), "Kurumsal", StringComparison.Ordinal);

        data["profileFound"] = false;
        data["profilePhone"] = string.Empty;

        if (isKurumsal)
        {
            data["profileGate"] = "pass";
        }
        else
        {
            data["profileGate"] = "not-found";
            data["otpAuthResult"] = "profile-not-found";
            data["otpAuthResultDetail"] = "No simple-profile record for the actor";
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
