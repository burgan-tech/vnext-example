using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// row-security-lab rs-reader: GetInstanceData (task 13) against a row-security-lab flow, driven by the
/// transition body: targetFlow (required) · targetDomain (default core) · useDapr · targetId or targetKey ·
/// headers { name: value } (absent → no mapping).
/// Output: readerData { done, isSuccess, statusCode, response }. Row security does NOT cover this task
/// (phase-1 known gap AR-1, pinned by K-3): an id excluded from GET still returns its data here.
/// </summary>
public class RsReaderDataMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var get = task as GetInstanceDataTask ?? throw new InvalidOperationException("Task must be a GetInstanceDataTask");
        var source = Source(context);

        get.SetDomain(Str(source, "targetDomain") ?? "core");
        get.SetFlow(Str(source, "targetFlow") ?? throw new InvalidOperationException("rs-reader: targetFlow is required"));
        if (string.Equals(Str(source, "useDapr"), "true", StringComparison.OrdinalIgnoreCase)) get.SetUseDapr(true);

        var id = Str(source, "targetId");
        if (!string.IsNullOrWhiteSpace(id)) get.SetInstance(id);
        else get.SetKey(Str(source, "targetKey") ?? throw new InvalidOperationException("rs-reader: targetId or targetKey is required"));

        var headers = MappedHeaders(source);
        if (headers.Count > 0) get.SetHeaders(headers);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        object? body = context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                readerData = new
                {
                    done = true,
                    isSuccess = Get(body, "isSuccess"),
                    statusCode = Get(body, "statusCode"),
                    response = Get(body, "data") ?? body
                }
            }
        });
    }

    private static IDictionary<string, object> Source(ScriptContext context)
    {
        object? body = context.Body;
        if (body is IDictionary<string, object> fromBody && fromBody.ContainsKey("targetFlow")) return fromBody;
        return context.Instance?.Data as IDictionary<string, object> ?? new Dictionary<string, object>();
    }

    private static Dictionary<string, string?> MappedHeaders(IDictionary<string, object> source)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (Get(source, "headers") is IDictionary<string, object> mapped)
        {
            foreach (var kv in mapped) headers[kv.Key] = kv.Value?.ToString();
        }
        return headers;
    }

    private static string? Str(IDictionary<string, object> source, string name) => Get(source, name)?.ToString();

    private static object? Get(object? node, string name)
    {
        if (node is IDictionary<string, object> dict)
            foreach (var kv in dict)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}
