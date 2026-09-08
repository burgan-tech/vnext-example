using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start transition bookkeeping. Normalises the caller-supplied payload into the derived fields the
/// rest of the flow relies on, and seeds every counter and the SIM-block latch.
/// <para>Runs on a script task, so InputHandler has nothing to prepare.</para>
/// </summary>
public class OtpAuthStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = OtpAuthHelpers.Merge(context.Instance?.Data);

        var phone = OtpAuthHelpers.Str(data, "user_phone");
        data["phonePrefix"] = OtpAuthHelpers.PhonePrefix(phone);
        data["phoneNumber"] = OtpAuthHelpers.PhoneSubscriber(phone);
        data["phoneCountryCode"] = "90";

        // Caller-tunable knobs; the schema defaults are re-applied here because a SubFlow start
        // payload may legitimately omit them.
        data["otpAttempt"] = OtpAuthHelpers.Int(data, "otpAttempt", 3);
        data["otpTtl"] = OtpAuthHelpers.Int(data, "otpTtl", 180);
        data["resendLimit"] = OtpAuthHelpers.Int(data, "resendLimit", 3);

        data["attemptUsed"] = 0;
        data["resendUsed"] = 0;
        data["simBlocked"] = false;
        data["otpType"] = "Otp";
        data["otpVerified"] = false;
        data["otpAuthResult"] = "in-progress";
        data["otpAuthResultDetail"] = "";

        return Task.FromResult(new ScriptResponse { Data = data });
    }
}
