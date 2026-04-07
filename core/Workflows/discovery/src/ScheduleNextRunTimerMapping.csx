using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions.Timer;

/// <summary>
/// Schedule Next Run Timer Mapping - Returns timer schedule for 1 hour delay
/// </summary>
public class ScheduleNextRunTimerMapping : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        try
        {
            // Return timer schedule for 1 hour from now
            return TimerSchedule.FromDateTime(DateTime.UtcNow.AddMinutes(3));
        }
        catch (Exception)
        {
            // Fallback to 1 hour on error
            return TimerSchedule.FromDateTime(DateTime.UtcNow.AddMinutes(3));
        }
    }
}

