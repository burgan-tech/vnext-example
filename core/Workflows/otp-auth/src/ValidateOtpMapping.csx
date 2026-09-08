using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// OTP verification and the attempt counter.
/// <para>
/// The wrong-code / allowance-spent split is decided here rather than in a rule, so the transition
/// rules stay pure equality checks and cannot disagree with each other.
/// </para>
/// <para>
/// The submitted code is dropped from instance data on the way out: a transition payload is merged
/// into the instance root, so leaving it in place would persist the one-time code.
/// </para>
/// </summary>
public class ValidateOtpMapping : ScriptBase, IMapping
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

        httpTask.SetBody(new
        {
            OtpCode = OtpAuthHelpers.Str(data, "otpCode", string.Empty),
            OtpReference = OtpAuthHelpers.Str(data, "otpReference", string.Empty),
            CitizenshipNumber = OtpAuthHelpers.Str(data, "actor", string.Empty),
            InstanceId = context.Instance?.Id.ToString()
        });

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);
        data["otpCode"] = null;

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var payload = envelope.ContainsKey("data") ? OtpAuthHelpers.ToDict(envelope["data"]) : envelope;
            var status = OtpAuthHelpers.Str(payload, "validateOtpStatus", string.Empty);

            if (string.Equals(status, "ValidateOtpSuccess", StringComparison.Ordinal))
            {
                data["otpVerified"] = true;
                data["verifyGate"] = "verified";
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (string.Equals(status, "ExpiredOtp", StringComparison.Ordinal))
            {
                data["verifyGate"] = "expired";
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            if (string.Equals(status, "InvalidOtp", StringComparison.Ordinal))
            {
                // The allowance is per instance and is never reset by a resend.
                var used = OtpAuthHelpers.Int(data, "attemptUsed", 0) + 1;
                var allowed = OtpAuthHelpers.Int(data, "otpAttempt", 3);
                data["attemptUsed"] = used;

                if (used >= allowed)
                {
                    data["verifyGate"] = "exceeded";
                    data["otpAuthResult"] = "attempt-exceeded";
                    data["otpAuthResultDetail"] = "Wrong code entered " + used + " time(s)";
                }
                else
                {
                    data["verifyGate"] = "retry";
                }

                return Task.FromResult(new ScriptResponse { Data = data });
            }

            data["verifyGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "Unexpected validateOtpStatus: " + status;
        }
        catch (Exception ex)
        {
            data["verifyGate"] = "failed";
            data["otpAuthResult"] = "technical-failure";
            data["otpAuthResultDetail"] = "validate-otp parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
