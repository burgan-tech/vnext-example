using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>subflow-carried-headers-lab child: route to the timer state when started with mode = "timer".</summary>
public class SfchModeTimerRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var mode = data != null && data.TryGetValue("mode", out var raw) && raw != null ? raw.ToString() : "";
        return Task.FromResult(mode == "timer");
    }
}
