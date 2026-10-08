using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>history-none-lab: Order-2 parallel branch, variableKey left.</summary>
public class HnLeftMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)["left"] = 1;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
