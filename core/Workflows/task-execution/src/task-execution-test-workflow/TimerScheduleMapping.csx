using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class TimerScheduleMapping : ScriptBase, ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        // Scheduled transition (triggerType: 2) icin Timer Task (tip 9) burada uretilir.
        // 3 sn sonrasini dondurerek runtime'in beklemesini sagliyoruz.
        var fireAt = DateTime.UtcNow.AddSeconds(3);
        LogInformation($"TimerScheduleMapping: scheduling at {fireAt:o} (3 seconds from now)");
        return Task.FromResult(TimerSchedule.FromDateTime(fireAt));
    }
}
