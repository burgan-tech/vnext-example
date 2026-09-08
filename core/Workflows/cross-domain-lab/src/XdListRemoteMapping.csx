using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Filtering;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 15 (GetInstances) -> partner/xd-remote over Dapr, filtered on attributes.testId with the
/// fluent InstanceQuery (the JSON `filter` key is string-only; the structured spec lives here).
/// Records how many remote instances matched and their testIds.
/// </summary>
public class XdListRemoteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var list = task as GetInstancesTask ?? throw new InvalidOperationException("Task must be a GetInstancesTask");
        var data = context.Instance?.Data as IDictionary<string, object>;
        var testId = data != null && data.TryGetValue("testId", out var t) ? t?.ToString() ?? string.Empty : string.Empty;

        list.SetDomain("partner");
        list.SetFlow("xd-remote");
        list.SetUseDapr(true);
        list.SetPage(1);
        list.SetPageSize(10);

        var spec = InstanceQuery.Create()
            .Where("attributes.testId", f => f.Eq(testId))
            .OrderByDescending("createdAt")
            .Build();
        list.SetFilterSpec(spec);

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var dto = Get(context.Body, "data") ?? context.Body;
        var items = Find(dto, "items") as IEnumerable;
        var count = 0;
        var testIds = new List<string?>();
        if (items != null)
        {
            foreach (var item in items)
            {
                count++;
                testIds.Add(Find(item, "testId")?.ToString());
            }
        }

        LogInformation($"XdListRemoteMapping: {count} remote instance(s) matched");
        return Task.FromResult(new ScriptResponse
        {
            Data = new { remoteCount = count, remoteListTestIds = testIds, remoteListed = true }
        });
    }

    private static object? Get(object? node, string name)
    {
        if (node is IDictionary<string, object> dict)
            foreach (var kv in dict)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static object? Find(object? node, params string[] names)
    {
        var queue = new Queue<object?>();
        queue.Enqueue(node);
        var visited = 0;
        while (queue.Count > 0 && visited++ < 500)
        {
            var current = queue.Dequeue();
            if (current is IDictionary<string, object> dict)
            {
                foreach (var name in names)
                    foreach (var kv in dict)
                        if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value != null) return kv.Value;
                foreach (var kv in dict) queue.Enqueue(kv.Value);
            }
            else if (current is IEnumerable list && current is not string)
            {
                foreach (var item in list) queue.Enqueue(item);
            }
        }
        return null;
    }
}
