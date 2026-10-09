using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// file-offload-xd: core fo-xd-parent starts partner fo-xd-child with a note only; no file is handed down.
/// OutputHandler copies nothing back, so a child handle can never reach the parent's data through the mapping —
/// the parent-data assertions then pin the forward path alone.
/// </summary>
public class FoXdParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        var target = (IDictionary<string, object>)input;
        target["note"] = "started-by-core-parent";
        target["parentInstanceId"] = context.Instance?.Id.ToString();
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });
}
