using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

/// <summary>
/// Arms the wait state's timeout <c>timerSeconds</c> out (seeded at start, default 20).
/// </summary>
public class ContractTimeoutTimer : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var seconds = 20;
        if (data != null && data.TryGetValue("timerSeconds", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out seconds);
        }

        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromSeconds(seconds)));
    }
}
