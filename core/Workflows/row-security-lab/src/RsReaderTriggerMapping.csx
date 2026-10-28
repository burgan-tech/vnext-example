using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// row-security-lab rs-reader: DirectTrigger (task 12) into a row-security-lab flow, driven by the
/// transition body: targetFlow (required) · targetDomain (default core) · useDapr · targetId or targetKey ·
/// transitionName (default rs-update) · headers { name: value } (absent → no mapping).
/// The target's updateData (rs-update) receives { touchedBy: "rs-reader" }.
/// Output: readerTrigger { done, isSuccess, statusCode, response }.
/// </summary>
public class RsReaderTriggerMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var trigger = task as DirectTriggerTask ?? throw new InvalidOperationException("Task must be a DirectTriggerTask");
        var source = Source(context);

        trigger.SetDomain(Str(source, "targetDomain") ?? "core");
        trigger.SetFlow(Str(source, "targetFlow") ?? throw new InvalidOperationException("rs-reader: targetFlow is required"));
        if (string.Equals(Str(source, "useDapr"), "true", StringComparison.OrdinalIgnoreCase)) trigger.SetUseDapr(true);
        trigger.SetSync(true);
        trigger.SetTransitionName(Str(source, "transitionName") ?? "rs-update");

        var id = Str(source, "targetId");
        if (!string.IsNullOrWhiteSpace(id)) trigger.SetInstance(id);
        else trigger.SetKey(Str(source, "targetKey") ?? throw new InvalidOperationException("rs-reader: targetId or targetKey is required"));

        trigger.SetBody(new { touchedBy = "rs-reader" });

        var headers = MappedHeaders(source);
        if (headers.Count > 0) trigger.SetHeaders(headers);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        object? body = context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                readerTrigger = new
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
