using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>history-none-subflow-lab: the child echoes the parentToken it was started with.</summary>
public class HnsChildEchoMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        dynamic result = new ExpandoObject();
        ((IDictionary<string, object>)result)["childEcho"] =
            data != null && data.TryGetValue("parentToken", out var token) ? token : null;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
