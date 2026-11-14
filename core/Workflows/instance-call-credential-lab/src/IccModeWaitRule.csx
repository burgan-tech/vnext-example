using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab icc-target: park in icc-t-wait when started with mode = "wait" (a target
/// the test starts itself, for the DirectTrigger and read cases). Any other start finishes at once.
/// </summary>
public class IccModeWaitRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var mode = data != null && data.TryGetValue("mode", out var raw) && raw != null ? raw.ToString() : "";
        return Task.FromResult(mode == "wait");
    }
}
