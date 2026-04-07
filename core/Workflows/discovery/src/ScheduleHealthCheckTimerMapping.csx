using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions.Timer;

/// <summary>
/// Schedule Health Check Timer Mapping - Returns timer schedule for 30 seconds delay
/// </summary>
public class ScheduleHealthCheckTimerMapping : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        try
        {
            // Return timer schedule for 30 seconds from now
            return TimerSchedule.FromDateTime(DateTime.UtcNow.AddSeconds(30));
        }
        catch (Exception)
        {
            // Fallback to 30 seconds on error
            return TimerSchedule.FromDateTime(DateTime.UtcNow.AddSeconds(30));
        }
    }
}

