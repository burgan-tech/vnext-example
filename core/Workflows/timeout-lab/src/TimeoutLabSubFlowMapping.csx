using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Minimal SubFlow mapping for the timeout lab: the scenario is about the child's DEADLINE, not
/// about data, so nothing is mapped in either direction beyond a marker the assertions can read.
/// </summary>
/// <remarks>
/// It exists because <c>subFlow.mapping</c> is required by the schema, not because the scenario
/// needs a transformation. Keeping it empty is deliberate — a mapping that moved real data would
/// give a failing test a second possible cause.
/// </remarks>
public class TimeoutLabSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        childInput.startedByTimeoutLabParent = true;
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null)
        {
            foreach (var kv in inst)
            {
                target[kv.Key] = kv.Value;
            }
        }
        target["childSettled"] = true;
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
