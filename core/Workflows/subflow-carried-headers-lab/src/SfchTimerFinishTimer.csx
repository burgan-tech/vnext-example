using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

/// <summary>
/// subflow-carried-headers-lab child: the timer that finishes a child started with mode = "timer".
/// Short so the test is quick; the timer hop has no caller request, so the child carries nothing.
/// </summary>
public class SfchTimerFinishTimer : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context) =>
        Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromSeconds(2)));
}
