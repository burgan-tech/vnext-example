using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Fires the root's SubProcess branch when the payload asks for it (<c>mode = "process"</c>).
/// A SubProcess is fire-and-forget: the root does not wait for it and nothing projects its state
/// upward, so the two become independent units of work and the list must carry BOTH.
/// </summary>
public class SpawnProcessRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        var spawn = data != null
                    && data.TryGetValue("mode", out var mode)
                    && mode != null
                    && mode.ToString() == "process";

        LogInformation($"SpawnProcessRule: spawn={spawn}");
        return Task.FromResult(spawn);
    }
}
