using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Exchanges the verified OTP for an authorization code.
/// <para>
/// CONTRACT NOT ANCHORED - the real issuer endpoint and field names were not confirmed during
/// planning (VNEXT-BUILD-PLAN.md section 9, S2). The request and response shapes below match the
/// MockLab fixture and must be revisited before this runs against a real service.
/// </para>
/// </summary>
public class IssueAuthorizationCodeMapping : ScriptBase, IMapping
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
            client_id = OtpAuthHelpers.Str(data, "client_id", string.Empty),
            client_secret = OtpAuthHelpers.Str(data, "client_secret", string.Empty),
            grant_type = OtpAuthHelpers.Str(data, "grant_type", string.Empty),
            scopeGroup = OtpAuthHelpers.Str(data, "scopeGroup", string.Empty),
            sub = OtpAuthHelpers.Str(data, "sub", string.Empty),
            actor = OtpAuthHelpers.Str(data, "actor", string.Empty),
            otpReference = OtpAuthHelpers.Str(data, "otpReference", string.Empty)
        });

        return new ScriptResponse();
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);

        try
        {
            var envelope = OtpAuthHelpers.ToDict(context.Body);
            var statusCode = OtpAuthHelpers.Int(envelope, "statusCode", 200);
            var payload = envelope.ContainsKey("data") ? OtpAuthHelpers.ToDict(envelope["data"]) : envelope;
            var code = OtpAuthHelpers.Str(payload, "authorizationCode");

            if (statusCode < 400 && code != null)
            {
                data["authorizationCode"] = code;
                data["authGate"] = "issued";
                data["otpAuthResult"] = "success";
                data["otpAuthResultDetail"] = string.Empty;
                // The secret has served its purpose; do not carry it into the success payload.
                data["client_secret"] = null;
                return Task.FromResult(new ScriptResponse { Data = data });
            }

            data["authorizationCode"] = string.Empty;
            data["authGate"] = "failed";
            data["otpAuthResult"] = "auth-code-failed";
            data["otpAuthResultDetail"] = "Authorization code request returned " + statusCode;
        }
        catch (Exception ex)
        {
            data["authorizationCode"] = string.Empty;
            data["authGate"] = "failed";
            data["otpAuthResult"] = "auth-code-failed";
            data["otpAuthResultDetail"] = "authorization-code parse error: " + ex.Message;
        }

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
