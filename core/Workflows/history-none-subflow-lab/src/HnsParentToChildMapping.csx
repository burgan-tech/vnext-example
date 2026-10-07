using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// history-none-subflow-lab: hands parentToken to the child and takes childEcho back as childResult.
/// </summary>
public class HnsParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        dynamic input = new ExpandoObject();
        ((IDictionary<string, object>)input)["parentToken"] =
            data != null && data.TryGetValue("parentToken", out var token) ? token : null;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic output = new ExpandoObject();
        ((IDictionary<string, object>)output)["childResult"] =
            body != null && body.TryGetValue("childEcho", out var echo) ? echo : null;
        return Task.FromResult(new ScriptResponse { Data = output });
    }
}
