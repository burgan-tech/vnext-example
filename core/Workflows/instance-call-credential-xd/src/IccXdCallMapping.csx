using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Filtering;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-xd icc-xd-parent (core): the one mapping behind every CROSS-DOMAIN trigger-task call —
/// SubProcess (14), Start (11), DirectTrigger (12), GetInstance (19), GetInstances (15), GetInstanceData (13) —
/// against <c>icc-xd-target</c> in the partner domain. Domain, flow and <c>useDapr</c> come from the task config;
/// this mapping sets only the identifier, the body and the header variant (the caller's instance data):
/// <list type="bullet">
///   <item><c>variant</c> absent or <c>none</c> — no task headers: the caller's credential must reach the target.</item>
///   <item><c>blank</c> — task header <c>sub = ""</c>: no <c>sub</c> reaches the target.</item>
///   <item><c>role-blank</c> — task header <c>role = ""</c>: the read runs without the caller's role.</item>
/// </list>
/// SubProcess / Start create the partner target under the key <c>{parentId}-proc</c> / <c>{parentId}-start</c>.
/// DirectTrigger and the reads address the test-started partner target by <c>targetId</c>; GetInstances finds
/// it by <c>testId</c>. OutputHandler records <c>call</c> (<c>targetInstanceId</c> — the id in the call's answer —, <c>isSuccess</c>, <c>statusCode</c>, <c>error</c>,
/// <c>hasSecret</c>, <c>secretNote</c>) — the same shape as instance-call-credential-lab's IccCallMapping.
/// </summary>
public class IccXdCallMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var id = context.Instance.Id.ToString();
        var headers = HeadersFor(Str(data, "variant") ?? "none");

        switch (task)
        {
            case SubProcessTask sub:
                // Explicit callee version, as cross-domain-lab's mappings do: with only the task config's `version`, the
                // Start call answered 404 from partner on the first lab run (the published config does not name a
                // version partner resolves).
                sub.SetVersion("1.0.3"); // = the generator's VERSION; keep in step
                sub.SetKey(id + "-proc");
                sub.SetBody(new { mode = "record", callerId = id });
                sub.SetSync(true);
                if (headers != null) sub.SetHeaders(headers);
                break;
            case StartTask start:
                start.SetVersion("1.0.3"); // = the generator's VERSION; keep in step
                start.SetKey(id + "-start");
                start.SetBody(new { mode = "record", callerId = id });
                start.SetSync(true);
                if (headers != null) start.SetHeaders(headers);
                break;
            case DirectTriggerTask trigger:
                trigger.SetInstance(Required(data, "targetId"));
                trigger.SetBody(new { touchedBy = id });
                if (headers != null) trigger.SetHeaders(headers);
                break;
            case GetInstanceTask get:
                get.SetInstance(Required(data, "targetId"));
                if (headers != null) get.SetHeaders(headers);
                break;
            case GetInstanceDataTask read:
                read.SetInstance(Required(data, "targetId"));
                if (headers != null) read.SetHeaders(headers);
                break;
            case GetInstancesTask list:
                var testId = Required(data, "testId");
                list.SetPage(1);
                list.SetPageSize(10);
                list.SetFilterSpec(InstanceQuery.Create().Where("attributes.testId", f => f.Eq(testId)).Build());
                if (headers != null) list.SetHeaders(headers);
                break;
            default:
                throw new InvalidOperationException($"instance-call-credential-xd: unexpected task type {task?.GetType().Name}");
        }

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        object body = context.Body;
        var secret = Find(Get(body, "data") ?? body, "secretNote");

        var call = new Dictionary<string, object>
        {
            ["done"] = true,
            ["isSuccess"] = Get(body, "isSuccess"),
            ["statusCode"] = Get(body, "statusCode"),
            ["error"] = Get(body, "errorMessage")?.ToString(),
            ["hasSecret"] = secret != null,
            ["secretNote"] = secret?.ToString(),
            ["state"] = context.Instance?.CurrentState,
            // The id in the call's answer — diagnostic only; the test reads the started instance by its key.
            ["targetInstanceId"] = Find(Get(body, "data") ?? body, "id")?.ToString()
        };

        var id = context.Instance.Id.ToString();
        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object>
            {
                ["call"] = call,
                ["startKey"] = id + "-start",
                ["procKey"] = id + "-proc"
            }
        });
    }

    private static Dictionary<string, string> HeadersFor(string variant) => variant switch
    {
        "none" => null,
        "blank" => new Dictionary<string, string> { ["sub"] = "" },
        "role-blank" => new Dictionary<string, string> { ["role"] = "" },
        _ => throw new InvalidOperationException($"instance-call-credential-xd: unknown task variant '{variant}'")
    };

    private static string Required(IDictionary<string, object> data, string name) =>
        Str(data, name) ?? throw new InvalidOperationException($"instance-call-credential-xd: instance data has no '{name}'");

    private static string Str(IDictionary<string, object> data, string name) =>
        Get(data, name)?.ToString();

    private static object Get(object node, string name)
    {
        if (node is IDictionary<string, object> dict)
            foreach (var kv in dict)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static object Find(object node, string name)
    {
        var queue = new Queue<object>();
        queue.Enqueue(node);
        var visited = 0;
        while (queue.Count > 0 && visited++ < 2000)
        {
            var current = queue.Dequeue();
            if (current is IDictionary<string, object> dict)
            {
                foreach (var kv in dict)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value != null) return kv.Value;
                    queue.Enqueue(kv.Value);
                }
            }
            else if (current is IEnumerable list && current is not string)
            {
                foreach (var item in list) queue.Enqueue(item);
            }
        }

        return null;
    }
}
