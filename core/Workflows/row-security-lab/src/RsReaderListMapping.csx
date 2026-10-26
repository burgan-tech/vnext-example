using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Filtering;
using BBT.Workflow.Scripting;

/// <summary>
/// row-security-lab rs-reader: GetInstances (task 15) against a row-security-lab flow, driven by the
/// transition body so one fixture covers every variant:
///   targetFlow (required) · targetDomain (default core) · useDapr · pageSize (default 100)
///   filter     raw filter string (plain filter or request envelope with groupBy); absent → testId eq
///   testId     used when filter is absent
///   headers    { name: value } added to the task's headers (x-branch-code, role, sub, act_sub, …);
///              absent → no mapping, the task carries only the caller's credential headers
/// Output: readerList { done, isSuccess, statusCode, count, ids, response }.
/// </summary>
public class RsReaderListMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var list = task as GetInstancesTask ?? throw new InvalidOperationException("Task must be a GetInstancesTask");
        var source = Source(context);

        list.SetDomain(Str(source, "targetDomain") ?? "core");
        list.SetFlow(Str(source, "targetFlow") ?? throw new InvalidOperationException("rs-reader: targetFlow is required"));
        list.SetPage(1);
        list.SetPageSize(int.TryParse(Str(source, "pageSize"), out var size) ? size : 100);
        if (string.Equals(Str(source, "useDapr"), "true", StringComparison.OrdinalIgnoreCase)) list.SetUseDapr(true);

        var filter = Str(source, "filter");
        if (!string.IsNullOrEmpty(filter))
        {
            list.SetFilter(filter);
        }
        else
        {
            var testId = Str(source, "testId") ?? string.Empty;
            list.SetFilterSpec(InstanceQuery.Create().Where("attributes.testId", f => f.Eq(testId)).Build());
        }

        var headers = MappedHeaders(source);
        if (headers.Count > 0) list.SetHeaders(headers);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        object? body = context.Body;
        var dto = Get(body, "data") ?? body;
        var ids = new List<string?>();
        if (Get(dto, "items") is IEnumerable items && items is not string)
        {
            foreach (var item in items) ids.Add(Get(item, "id")?.ToString());
        }

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                readerList = new
                {
                    done = true,
                    isSuccess = Get(body, "isSuccess"),
                    statusCode = Get(body, "statusCode"),
                    count = ids.Count,
                    ids,
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
