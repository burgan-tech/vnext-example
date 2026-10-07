using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>history-none-lab: Writes seeded=true (order 1).</summary>
public class HnSeedMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)["seeded"] = true;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
