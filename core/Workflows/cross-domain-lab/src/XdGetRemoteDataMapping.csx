using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 13 (GetInstanceData) -> partner/xd-remote over Dapr. Copies the remote instance data
/// (which carries the testId we started it with) into the parent as remoteData.
/// </summary>
public class XdGetRemoteDataMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var get = task as GetInstanceDataTask ?? throw new InvalidOperationException("Task must be a GetInstanceDataTask");
        var data = context.Instance?.Data as IDictionary<string, object>;

        get.SetDomain("partner");
        get.SetFlow("xd-remote");
        get.SetUseDapr(true);

        if (data != null && data.TryGetValue("remoteInstanceId", out var id) && !string.IsNullOrWhiteSpace(id?.ToString()))
            get.SetInstance(id!.ToString());
        else if (data != null && data.TryGetValue("remoteKey", out var key))
            get.SetKey(key?.ToString());
        else
            throw new InvalidOperationException("xd-parent data carries neither remoteInstanceId nor remoteKey");

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Body = { isSuccess, data: <GetInstanceDataOutput { data, extensions, ... }>, ... }
        var dto = Get(context.Body, "data") ?? context.Body;
        var remoteData = Get(dto, "data") ?? Find(dto, "attributes") ?? dto;
        LogInformation($"XdGetRemoteDataMapping: remote data read (type={remoteData?.GetType().Name})");
        return Task.FromResult(new ScriptResponse { Data = new { remoteData, remoteDataRead = true } });
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
