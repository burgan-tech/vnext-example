using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// file-streaming-lab: DaprBinding "list" on fsl-minio (vnext-client-sdk-core#142-B). The task's answer reaches this
/// output mapping: the raw JSON text and the encoding the runtime named.
/// </summary>
public class FslBindingListMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) => Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        dynamic result = new ExpandoObject();
        result.listing = new Dictionary<string, object?>
        {
            ["success"] = Get(body, "isSuccess"),
            ["encoding"] = Get(Get(body, "metadata") as IDictionary<string, object>, "DataEncoding"),
            ["text"] = Get(body, "body"),
            ["error"] = Get(body, "errorMessage"),
        };
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static object? Get(IDictionary<string, object>? d, string key) => d != null && d.TryGetValue(key, out var v) ? v : null;
}
