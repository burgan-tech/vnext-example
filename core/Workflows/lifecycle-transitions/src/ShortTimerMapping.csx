using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class ShortTimerMapping : ScriptBase, ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        LogInformation("ShortTimerMapping: scheduling 10 seconds from now");
        return new TimerSchedule
        {
            FireAt = DateTime.UtcNow.AddSeconds(10)
        };
    }
}
