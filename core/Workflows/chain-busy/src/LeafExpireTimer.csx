using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;

// leaf-waiting'e uzun bir zamanlayici kurar. Test suresince ASLA atesleme yapmaz —
// gorevi ARMED bir InstanceJob birakmaktir: ExecuteAt degeri sabit kaliyorsa, arada
// calisan `$self` transition zamanlayiciyi yeniden kurmamis demektir.
public class LeafExpireTimer : ITimerMapping
{
    public Task<TimerSchedule> Handler(ScriptContext context)
    {
        return Task.FromResult(TimerSchedule.FromDuration(TimeSpan.FromMinutes(30)));
    }
}
