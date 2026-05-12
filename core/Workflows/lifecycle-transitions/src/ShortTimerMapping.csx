using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class ShortTimerMapping : ScriptBase, ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        LogInformation("ShortTimerMapping: scheduling 6 seconds from now");
        return TimerSchedule.FromDateTime(DateTime.UtcNow.AddSeconds(6));
    }
}
