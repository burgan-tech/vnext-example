using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>file-offload-lab: fo-parent starts fo-child with a note only; no file is handed down.</summary>
public class FoParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        ((IDictionary<string, object>)input)["note"] = "started-by-parent";
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });
}
