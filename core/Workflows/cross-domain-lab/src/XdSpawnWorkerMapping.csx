using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 14 (SubProcess) -> partner/xd-worker, fire-and-forget over Dapr service invocation.
/// Output records the started worker instance id so the test can read it back from the partner.
/// </summary>
public class XdSpawnWorkerMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask ?? throw new InvalidOperationException("Task must be a SubProcessTask");
        var data = context.Instance?.Data as IDictionary<string, object>;
        var testId = data != null && data.TryGetValue("testId", out var t) ? t?.ToString() : context.Instance?.Id.ToString();

        sub.SetDomain("partner");
        sub.SetFlow("xd-worker");
        sub.SetVersion("1.0.0");
        sub.SetUseDapr(true);
        sub.SetKey($"{testId}-worker");
        sub.SetSync(false);
        sub.SetBody(new { testId, parentInstanceId = context.Instance?.Id, parentDomain = "core" });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var id = Find(context.Body, "id", "instanceId", "subFlowInstanceId")?.ToString();
        LogInformation($"XdSpawnWorkerMapping: worker started, id={id ?? "<not found>"} bodyType={context.Body?.GetType().Name}");
        return Task.FromResult(new ScriptResponse { Data = new { workerInstanceId = id, workerSpawned = true } });
    }

    /// <summary>Breadth-first, case-insensitive property lookup over the JSON-shaped dynamic body.</summary>
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
