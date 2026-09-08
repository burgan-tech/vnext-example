using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;

/// <summary>
/// Abandonment guard for otp-awaiting-resend: if the user never comes back to press "resend", the
/// instance must not stay open forever.
/// </summary>
public class AbandonTimer : ScriptBase, ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromMinutes(5)));
    }
}
