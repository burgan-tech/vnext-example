using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// OTP send, shared by the otp-sending and otp-resending-fast states, and the home of the
/// SIM-block LATCH.
/// <para>
/// The latch is the whole reason one mapping serves both states. InputHandler always sends
/// <c>OtpType = data.otpType</c>. The first OtpBlacklisted answer for a Kurumsal caller flips
/// otpType to "Fast" and sets simBlocked. Because the escalate-fast gate additionally requires
/// <c>simBlocked == false</c>, a second block can only produce "blocked" - so FAST is attempted at
/// most once, and plain "Otp" is never attempted again for the life of the instance.
/// </para>
/// <para>Bireysel never satisfies the escalation condition, so FAST is unreachable for it by
/// construction rather than by a guard that could be forgotten.</para>
/// </summary>
public class SendOtpMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        ArgumentNullException.ThrowIfNull(httpTask);

        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var apiBaseUrl = GetConfigValue("Example:ApiBaseUrl", "http://localhost:3001");
        httpTask.SetUrl(httpTask.Url.Replace("API_BASEURL", apiBaseUrl));

        var apiKey = await GetSecretAsync("vnext-secret", "workflow-secret", "ApiSecret");
        httpTask.AddHeader("X-APISIX-KEY", apiKey ?? string.Empty);

        // CitizenshipNumber is the ACTOR, not sub: for a Kurumsal caller sub is the legal entity
        // number, while the SMS goes to the natural person acting on its behalf.
        httpTask.SetBody(new
        {
            Phone = new
            {
                Number = OtpAuthHelpers.Str(data, "phoneNumber", string.Empty),
                Prefix = OtpAuthHelpers.Str(data, "phonePrefix", string.Empty),
                CountryCode = OtpAuthHelpers.Str(data, "phoneCountryCode", "90")
            },
            CitizenshipNumber = OtpAuthHelpers.Str(data, "actor", string.Empty),
            OtpRetryAttempt = OtpAuthHelpers.Int(data, "otpAttempt", 3),
            OtpDuration = OtpAuthHelpers.Int(data, "otpTtl", 180),
            Language = OtpAuthHelpers.Str(data, "language", "tr-TR"),
            MessageTemplate = OtpAuthHelpers.Str(data, "messageTemplate"),
            OtpType = OtpAuthHelpers.Str(data, "otpType", "Otp"),
            InstanceId = context.Instance?.Id.ToString(),
            SmsSender = OtpAuthHelpers.Str(data, "smsSender"),
            AppType = OtpAuthHelpers.Str(data, "appType"),
            ProcessName = "otp-auth",
            ProcessIdentity = OtpAuthHelpers.Str(data, "actor", string.Empty),
            Tags = (object)null
        });

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var payload = envelope.ContainsKey("data") ? OtpAuthHelpers.ToDict(envelope["data"]) : envelope;
            var sendInfo = payload.ContainsKey("sendInfo") ? OtpAuthHelpers.ToDict(payload["sendInfo"]) : new Dictionary<string, object>();

            var status = OtpAuthHelpers.Str(payload, "sendOtpStatus", string.Empty);
            data["sendOtpStatus"] = status;
            data["smsTraceId"] = OtpAuthHelpers.Str(sendInfo, "smsTraceId", string.Empty);

            if (string.Equals(status, "SendOtpSuccess", StringComparison.Ordinal))
            {
                var ttl = OtpAuthHelpers.Int(data, "otpTtl", 180);
                data["otpReference"] = OtpAuthHelpers.Str(payload, "otpReference", string.Empty);
                data["otpSentAt"] = OtpAuthHelpers.UtcNow();
                // Absolute deadline: the TTL timer derives its duration from this, so retrying a
                // wrong code cannot buy the user a fresh validity window.
                data["otpExpiresAt"] = OtpAuthHelpers.UtcIn(ttl);
                data["sendGate"] = "sent";
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (string.Equals(status, "OtpBlacklisted", StringComparison.Ordinal))
            {
                var isKurumsal = string.Equals(OtpAuthHelpers.Str(data, "scopeGroup", "Bireysel"), "Kurumsal", StringComparison.Ordinal);
                var alreadyLatched = OtpAuthHelpers.Bool(data, "simBlocked", false);

                if (isKurumsal && !alreadyLatched)
                {
                    data["simBlocked"] = true;
                    data["simBlockedAt"] = OtpAuthHelpers.UtcNow();
                    data["otpType"] = "Fast";
                    data["sendGate"] = "escalate-fast";
                    return Task.FromResult(new ScriptResponse { Data = data });
                }

                data["simBlocked"] = true;
                data["sendGate"] = "blocked";
                data["otpAuthResult"] = "sim-blocked";
                data["otpAuthResultDetail"] = OtpAuthHelpers.Str(sendInfo, "message", "SIM blocked for OTP delivery");
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            data["sendGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "Unexpected sendOtpStatus: " + status;
        }
        catch (Exception ex)
        {
            data["sendGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "send-otp parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
