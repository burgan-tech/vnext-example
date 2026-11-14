using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// instance-call-credential-lab: the RECORDER every target runs on the hop under test. It writes which
/// credential headers (<c>sub</c>, <c>act_sub</c>, <c>position</c>, <c>client_id</c>, <c>role</c>) were
/// present in <c>context.Headers</c> and their values, plus the instance's own id.
/// <para>
/// One mapping serves two hops: the start hop (onEntry of the start target) writes <c>seen</c>; the
/// <c>touch</c> transition of icc-target (driven by the parent's DirectTrigger task, its onExecutionTasks
/// run while the instance is still in <c>icc-t-wait</c>) writes <c>touchSeen</c>.
/// </para>
/// <para>
/// <c>context.Headers</c> is read as a dictionary, never as a dynamic member: reading a header that is
/// not there off the dynamic object throws. Keys are lower-cased by the runtime; the lookup is
/// case-insensitive anyway.
/// </para>
/// </summary>
public class IccRecordMapping : ScriptBase, IMapping
{
    private static readonly string[] Credentials = { "sub", "act_sub", "position", "client_id", "role" };

    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context) =>
        Task.FromResult(new ScriptResponse());

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var headers = Read((object)context.Headers);

        var seen = new Dictionary<string, object>();
        foreach (var name in Credentials)
        {
            var present = headers.TryGetValue(name, out var value);
            seen[name] = present ? value : null;
            seen["has_" + name] = present;
        }

        var keys = new List<string>(headers.Keys);
        keys.Sort(StringComparer.Ordinal);
        seen["keys"] = string.Join(",", keys);

        var field = (context.Instance?.CurrentState ?? "") == "icc-t-wait" ? "touchSeen" : "seen";
        var result = new Dictionary<string, object>
        {
            [field] = seen,
            ["selfId"] = context.Instance?.Id.ToString()
        };
        return Task.FromResult(new ScriptResponse { Data = result });
    }

    private static Dictionary<string, string> Read(object headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is IDictionary<string, string> typed)
        {
            foreach (var pair in typed) result[pair.Key] = pair.Value;
        }
        else if (headers is IDictionary<string, object> loose)
        {
            foreach (var pair in loose) result[pair.Key] = pair.Value?.ToString();
        }

        return result;
    }
}
