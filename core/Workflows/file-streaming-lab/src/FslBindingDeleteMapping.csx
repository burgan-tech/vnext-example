using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// file-streaming-lab: deletes files[0]'s object with a DaprBinding task (vnext-client-sdk-core#142-D — the runtime has
/// no delete endpoint; retention is the flow's own logic). The key is named at run time with FileObjectKey.
/// </summary>
public class FslBindingDeleteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var files = data != null && data.TryGetValue("files", out var raw) ? raw as IList<object> : null;
        var first = files != null && files.Count > 0 ? files[0] as IDictionary<string, object> : null;
        var file = first != null && first.TryGetValue("file", out var f) ? f?.ToString() ?? "" : "";

        var binding = (DaprBindingTask)task;
        binding.SetMetadata(new Dictionary<string, string?> { ["key"] = FileObjectKey(file) });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic result = new ExpandoObject();
        result.deleted = new Dictionary<string, object?>
        {
            ["success"] = body != null && body.TryGetValue("isSuccess", out var ok) ? ok : null,
            ["error"] = body != null && body.TryGetValue("errorMessage", out var e) ? e : null,
        };
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
