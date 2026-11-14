using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-xd icc-xd-sub-nest (partner), XC-06: a GetInstance (19) from the partner child BACK into
/// core (task config <c>domain: core</c>, <c>useDapr: true</c>) on the core target <c>coreTargetId</c>. The mapping
/// writes NO headers.
/// <para>
/// A read records nothing on its target, so the probe is the target's schema: <c>starterNote</c> carries
/// <c>x-roles</c> allow <c>$InstanceBehalfOfStarter</c> — visible only to a caller whose <c>sub</c> equals the
/// target's <c>CreatedByBehalfOf</c> (the test starts the target with the caller's own <c>sub</c>). The read returns
/// <c>starterNote</c> exactly when the call carried that <c>sub</c>.
/// </para>
/// OutputHandler records <c>backRead</c> (<c>isSuccess</c>, <c>statusCode</c>, <c>hasStarterNote</c>).
/// </summary>
public class IccXdBackGetMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var get = task as GetInstanceTask ?? throw new InvalidOperationException("Task must be a GetInstanceTask");
        get.SetInstance(Get(context.Instance?.Data, "coreTargetId")?.ToString()
            ?? throw new InvalidOperationException("instance-call-credential-xd: the nested child has no 'coreTargetId'"));
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        object body = context.Body;
        var note = Find(Get(body, "data") ?? body, "starterNote");
        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object>
            {
                ["backRead"] = new Dictionary<string, object>
                {
                    ["done"] = true,
                    ["isSuccess"] = Get(body, "isSuccess"),
                    ["statusCode"] = Get(body, "statusCode"),
                    ["error"] = Get(body, "errorMessage")?.ToString(),
                    ["hasStarterNote"] = note != null,
                    ["starterNote"] = note?.ToString()
                }
            }
        });
    }

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
