using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>history-none-lab: Order-2 parallel branch, variableKey right.</summary>
public class HnRightMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)["right"] = 2;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
