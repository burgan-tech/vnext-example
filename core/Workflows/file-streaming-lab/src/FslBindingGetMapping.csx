using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// file-streaming-lab: DaprBinding "get" of files[0] on fsl-minio (vnext-client-sdk-core#142-B/D). The input mapping
/// names the object at run time (SetMetadata + FileObjectKey); the output mapping reads the binary answer (base64).
/// </summary>
public class FslBindingGetMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var binding = (DaprBindingTask)task;
        binding.SetOperation("get");
        binding.SetMetadata(new Dictionary<string, string?> { ["key"] = FileObjectKey(FirstFile(context)) });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
        var data = Get(body, "data") as string;
        dynamic result = new ExpandoObject();
        result.got = new Dictionary<string, object?>
        {
            ["encoding"] = Get(Get(body, "metadata") as IDictionary<string, object>, "DataEncoding"),
            ["length"] = data == null ? -1 : Convert.FromBase64String(data).Length,
            ["base64Prefix"] = data == null ? null : data.Substring(0, Math.Min(16, data.Length)),
        };
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    internal static string FirstFile(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        var files = data != null && data.TryGetValue("files", out var raw) ? raw as IList<object> : null;
        var first = files != null && files.Count > 0 ? files[0] as IDictionary<string, object> : null;
        return first != null && first.TryGetValue("file", out var f) ? f?.ToString() ?? "" : "";
    }

    private static object? Get(IDictionary<string, object>? d, string key) => d != null && d.TryGetValue(key, out var v) ? v : null;
}
