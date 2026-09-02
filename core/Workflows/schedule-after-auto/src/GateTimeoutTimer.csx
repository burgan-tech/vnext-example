using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

/// <summary>
/// Arms the gate's scheduled transition a few seconds out.
/// <para>
/// Short on purpose: the no-auto-winner path must both SEE the armed entry in the state function
/// and watch it fire within one test. Long enough that the auto-winner path's chained hop
/// (~2.5s, see AutoAdvanceMapping) cannot overlap a fire.
/// </para>
/// </summary>
public class GateTimeoutTimer : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromSeconds(8)));
    }
}
