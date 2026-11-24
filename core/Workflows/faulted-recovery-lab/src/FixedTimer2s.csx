using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;

/// <summary>faulted-recovery-lab: the timer of fr-timed — fires 2 seconds after the state is entered.</summary>
public class FixedTimer2s : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context) =>
        Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromSeconds(2)));
}
