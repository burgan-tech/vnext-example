using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 11 (Start) -> partner/xd-remote, sync. Records remoteInstanceId (from the start response) and
/// remoteKey (the deterministic key we started it with, used as fallback identifier downstream).
/// </summary>
public class XdStartRemoteMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var start = task as StartTask ?? throw new InvalidOperationException("Task must be a StartTask");
        var data = context.Instance?.Data as IDictionary<string, object>;
        var testId = data != null && data.TryGetValue("testId", out var t) ? t?.ToString() : context.Instance?.Id.ToString();

        start.SetDomain("partner");
        start.SetFlow("xd-remote");
        start.SetVersion("1.0.0");
        start.SetUseDapr(true);
        start.SetSync(true);
        start.SetKey($"{testId}-remote");
        start.SetBody(new { testId, parentInstanceId = context.Instance?.Id, parentDomain = "core" });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var testId = data != null && data.TryGetValue("testId", out var t) ? t?.ToString() : context.Instance?.Id.ToString();
        var id = Find(context.Body, "id", "instanceId")?.ToString();
        LogInformation($"XdStartRemoteMapping: remote started, id={id ?? "<not found>"} bodyType={context.Body?.GetType().Name}");
        return Task.FromResult(new ScriptResponse
        {
            Data = new { remoteInstanceId = id, remoteKey = $"{testId}-remote", remoteStarted = true }
        });
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
