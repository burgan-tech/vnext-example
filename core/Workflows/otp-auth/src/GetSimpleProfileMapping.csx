using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// simple-profile lookup by <c>actor</c>, plus the profileGate decision.
/// <para>
/// MockLab has no route-parameter support, so the customer id travels as a query string rather than
/// as a path segment (see VNEXT-BUILD-PLAN.md risk R12).
/// </para>
/// <para>
/// 404 and 400 mean "no record", not a transport error - the Asgard client this contract came from
/// returns null for both. They are handled here when the runtime hands the response to the mapping,
/// and by the state's errorBoundary when the runtime raises them instead.
/// </para>
/// </summary>
public class GetSimpleProfileMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        ArgumentNullException.ThrowIfNull(httpTask);

        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        var actor = OtpAuthHelpers.Str(data, "actor", string.Empty);

        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl) + "?customerId=" + Uri.EscapeDataString(actor));

        // Lab secret store may not carry the key at all; an absent secret must not break the call.
        var apiKey = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
        httpTask.AddHeader("X-APISIX-KEY", apiKey ?? string.Empty);

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        var scopeGroup = OtpAuthHelpers.Str(data, "scopeGroup", "Bireysel");
        var isKurumsal = string.Equals(scopeGroup, "Kurumsal", StringComparison.Ordinal);

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var statusCode = OtpAuthHelpers.Int(envelope, "statusCode", 200);
            var payload = envelope.ContainsKey("data")
                ? OtpAuthHelpers.ToDict(envelope["data"])
                : envelope;

            if (statusCode == 404 || statusCode == 400)
            {
                Apply(data, isKurumsal, false, null);
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (statusCode >= 500)
            {
                data["profileGate"] = "failed";
                data["otpAuthResult"] = "technical-failure";
                data["otpAuthResultDetail"] = "simple-profile returned " + statusCode;
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            var profilePhone = OtpAuthHelpers.Str(payload, "phoneNumber");
            data["profileEmail"] = OtpAuthHelpers.Str(payload, "email", string.Empty);
            Apply(data, isKurumsal, profilePhone != null, profilePhone);
        }
        catch (Exception ex)
        {
            // Total switch: an unreadable response must still leave a gate value behind, otherwise
            // no transition rule matches and the instance hangs silently.
            data["profileGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "simple-profile parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }

    /// <summary>
    /// Kurumsal treats the profile as informational only: neither a missing record nor a phone
    /// mismatch stops the flow, because the caller supplies the phone for the legal entity's user.
    /// </summary>
    private static void Apply(IDictionary<string, object> data, bool isKurumsal, bool found, string profilePhone)
    {
        data["profileFound"] = found;
        data["profilePhone"] = profilePhone ?? string.Empty;

        if (isKurumsal)
        {
            data["profileGate"] = "pass";
            return;
        }

        if (!found)
        {
            data["profileGate"] = "not-found";
            data["otpAuthResult"] = "profile-not-found";
            data["otpAuthResultDetail"] = "No simple-profile record for the actor";
            return;
        }

        if (!OtpAuthHelpers.PhonesMatch(OtpAuthHelpers.Str(data, "user_phone"), profilePhone))
        {
            data["profileGate"] = "phone-mismatch";
            data["otpAuthResult"] = "phone-mismatch";
            data["otpAuthResultDetail"] = "Supplied phone does not match the profile phone";
            return;
        }

        data["profileGate"] = "pass";
    }
}
