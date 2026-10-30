using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>subflow-carried-headers-lab child: finish at once when started with mode = "auto" (child B).</summary>
public class SfchModeAutoRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var mode = data != null && data.TryGetValue("mode", out var raw) && raw != null ? raw.ToString() : "";
        return Task.FromResult(mode == "auto");
    }
}
