using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;

/// <summary>
/// eb-scripts (vnext#1268): a timer script that fails when the state is entered. Scheduling fails the
/// hop and the instance faults; the incident must keep the script's trace.
/// </summary>
public class EbThrowTimer : ScriptBase, ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(WindowOf(context));
    }

    private static TimerSchedule WindowOf(ScriptContext context) =>
        throw new InvalidOperationException("eb-scripts: the timer script has no window");
}
