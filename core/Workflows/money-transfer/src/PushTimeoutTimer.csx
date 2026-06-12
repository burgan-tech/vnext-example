using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

// Timer for the push-timeout transition (triggerType: 2). Fires 5 minutes (PT5M) after entering
// awaiting-push-approval if the user has not approved from their device, routing to transfer-failed.
public class PushTimeoutTimer : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromMinutes(5)));
    }
}
