using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Parent side of the SubFlow contract.
/// <para>
/// InputHandler projects only the start-payload fields, so the child never receives unrelated parent
/// state. OutputHandler merges back only the agreed outcome fields, and deliberately leaves
/// client_secret and otpCode behind.
/// </para>
/// </summary>
public class HostToOtpAuthSubFlowMapping : ScriptBase, ISubFlowMapping
{
    private static readonly string[] Inbound =
    {
        "sub", "actor", "client_id", "grant_type", "client_secret",
        "user_phone", "user_email", "user_name", "user_surname",
        "scopeGroup", "otpAttempt", "otpTtl", "resendLimit"
    };

    private static readonly string[] Outbound =
    {
        "otpAuthResult", "otpAuthResultDetail", "authorizationCode",
        "otpReference", "attemptUsed", "resendUsed", "simBlocked"
    };

    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var source = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var payload = new Dictionary<string, object>();

        foreach (var field in Inbound)
        {
            if (source.TryGetValue(field, out var value) && value != null)
            {
                payload[field] = value;
            }
        }

        return Task.FromResult(new ScriptResponse { Data = payload });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var child = OtpAuthHelpers.ToDict(context.Body);

        foreach (var field in Outbound)
        {
            if (child.TryGetValue(field, out var value))
            {
                data[field] = value;
            }
        }

        if (!data.ContainsKey("otpAuthResult") || data["otpAuthResult"] == null)
        {
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "SubFlow returned no outcome";
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
