using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-id-filter-lab idf-reader: GetInstances (task 15) with a raw filter taken from the transition
/// body, so one fixture covers every id-filter case of vnext#1080:
///   targetDomain (required — the suite may run on a side domain) · targetFlow (default idf-target) · filter (required)
/// Output: readerList { done, isSuccess, statusCode, error, count, ids }. A failed read is recorded, not thrown:
/// the flow has no error boundary, so this record is where a rejected filter shows.
/// </summary>
public class IdfReaderListMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var list = task as GetInstancesTask ?? throw new InvalidOperationException("Task must be a GetInstancesTask");
        var source = context.Body as IDictionary<string, object> ?? new Dictionary<string, object>();

        list.SetDomain(Str(source, "targetDomain") ?? throw new InvalidOperationException("idf-reader: targetDomain is required"));
        list.SetFlow(Str(source, "targetFlow") ?? "idf-target");
        list.SetPage(1);
        list.SetPageSize(50);
        list.SetFilter(Str(source, "filter") ?? throw new InvalidOperationException("idf-reader: filter is required"));
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
                    error = Get(body, "errorMessage")?.ToString(),
                    count = ids.Count,
                    ids
                }
            }
        });
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
