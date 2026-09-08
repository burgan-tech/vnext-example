using System;
using System.Threading.Tasks;
using Acme.Helpers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;

/// <summary>
/// TTL timer for otp-awaiting-code, rebuilt on every entry into that state.
/// <para>
/// The duration is derived from the ABSOLUTE <c>otpExpiresAt</c> stamped when the OTP was sent, not
/// from otpTtl directly. A fixed FromDuration(otpTtl) would restart the clock on every wrong-code
/// round trip, keeping the flow alive long after the SMS code itself had died.
/// </para>
/// </summary>
public class OtpTtlTimer : ScriptBase, ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        var data = OtpAuthHelpers.ToDict(context.Instance?.Data);
        var ttl = OtpAuthHelpers.Int(data, "otpTtl", 180);
        var expiresAt = OtpAuthHelpers.ParseUtc(OtpAuthHelpers.Str(data, "otpExpiresAt"));

        var remaining = expiresAt.HasValue
            ? expiresAt.Value - DateTime.UtcNow
            : TimeSpan.FromSeconds(ttl);

        // Two seconds of grace so a verify-otp call racing the timer usually wins; if it loses, the
        // server-side expired branch reaches the same gate anyway.
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.FromSeconds(2);
        }

        if (remaining > TimeSpan.FromSeconds(ttl))
        {
            remaining = TimeSpan.FromSeconds(ttl);
        }

        return Task.FromResult(TimerSchedule.FromDuration(remaining));
    }
}
