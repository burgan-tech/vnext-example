using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

/// <summary>
/// caller-roles-lab: arms the review window by the caller's resolved role — 48 h for
/// <c>rule-schema.approver</c>, 24 h otherwise. Timers are computed when the state is ENTERED; a state entered
/// by a system hop (a fired timer re-arming, a subflow resume) has no caller, so the script guards IsAvailable.
/// </summary>
public class ApproverWindowTimer : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var approver = context.Caller.IsAvailable && await context.Caller.HasRoleAsync("rule-schema.approver");
        return TimerSchedule.FromDuration(TimeSpan.FromHours(approver ? 48 : 24));
    }
}
