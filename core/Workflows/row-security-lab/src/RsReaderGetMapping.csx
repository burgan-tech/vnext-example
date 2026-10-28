using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// row-security-lab rs-reader: GetInstance (task 19) against a row-security-lab flow, driven by the
/// transition body: targetFlow (required) · targetDomain (default core) · useDapr · targetId or targetKey ·
/// headers { name: value } (absent → no mapping).
/// Output: readerGet { done, isSuccess, statusCode, id, response }. A row the caller cannot see answers
/// like a missing instance (404); the task result is recorded, not thrown.
/// </summary>
public class RsReaderGetMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var get = task as GetInstanceTask ?? throw new InvalidOperationException("Task must be a GetInstanceTask");
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
        var dto = Get(body, "data") ?? body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                readerGet = new
                {
                    done = true,
                    isSuccess = Get(body, "isSuccess"),
                    statusCode = Get(body, "statusCode"),
                    id = Get(dto, "id")?.ToString(),
                    response = dto
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
