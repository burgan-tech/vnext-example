using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Records the submitted decision on the approve / reject transitions.
/// <para>
/// DELTA-ONLY: returns only the keys it owns. A full echo would overwrite concurrent writers'
/// fresh values with a stale snapshot; the merge keeps the head as it is.
/// </para>
/// </summary>
public class DecisionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;

        var decision = "unknown";
        if (body != null && body.TryGetValue("decision", out var raw) && raw != null)
        {
            decision = raw.ToString();
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["decision"] = decision;

        LogInformation($"DecisionMapping: decision recorded as {decision}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
