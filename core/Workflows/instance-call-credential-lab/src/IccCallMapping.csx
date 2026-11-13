using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Filtering;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab: the one mapping behind every trigger-task call the lab makes — SubProcess
/// (14), Start (11), DirectTrigger (12), GetInstance (19), GetInstances (15), GetInstanceData (13) —
/// all same-domain against icc-target. The task type picks what to set up; the caller's instance data
/// picks the header variant:
/// <list type="bullet">
///   <item><c>variant</c> absent or <c>none</c> — no task headers: the caller's credential must reach the target.</item>
///   <item><c>blank</c> — task header <c>sub = ""</c>: no <c>sub</c> reaches the target.</item>
///   <item><c>role-blank</c> — task header <c>role = ""</c>: the read runs without the caller's role.</item>
/// </list>
/// SubProcess / Start create the target under the key <c>{callerId}-proc</c> / <c>{callerId}-start</c> (the
/// nested SubFlow child uses this mapping too, with its own id). DirectTrigger and the reads address the
/// test-started target by <c>targetId</c>; GetInstances finds it by <c>testId</c>.
/// <para>
/// OutputHandler records the result as <c>call</c> (<c>isSuccess</c>, <c>statusCode</c>, <c>error</c>) and,
/// for the reads, whether the role-protected field <c>secretNote</c> (<c>x-roles</c> allow <c>icc.reader</c>)
/// was anywhere in what came back (<c>hasSecret</c>, <c>secretNote</c>).
/// </para>
/// </summary>
public class IccCallMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var id = context.Instance.Id.ToString();
        var headers = HeadersFor(Str(data, "variant") ?? "none");

        switch (task)
        {
            case SubProcessTask sub:
                sub.SetKey(id + "-proc");
                sub.SetBody(new { mode = "record", callerId = id });
                sub.SetSync(true);
                if (headers != null) sub.SetHeaders(headers);
                break;
            case StartTask start:
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
                throw new InvalidOperationException($"instance-call-credential-lab: unexpected task type {task?.GetType().Name}");
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
            ["state"] = context.Instance?.CurrentState
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
        _ => throw new InvalidOperationException($"instance-call-credential-lab: unknown task variant '{variant}'")
    };

    private static string Required(IDictionary<string, object> data, string name) =>
        Str(data, name) ?? throw new InvalidOperationException($"instance-call-credential-lab: instance data has no '{name}'");

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
