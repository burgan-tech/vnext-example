using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Minimal SubFlow mapping for the display-labels lab: the scenario is about what the read
/// surfaces DESCRIBE, not about data, so nothing is mapped beyond a marker.
/// </summary>
/// <remarks>
/// It exists because <c>subFlow.mapping</c> is required by the schema. Keeping it empty is
/// deliberate — a mapping that moved real data would give a failing test a second possible cause.
/// </remarks>
public class DisplayLabelsLabSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        childInput.startedByDisplayLabelsLab = true;
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        if (context.Instance.Data is IDictionary<string, object> inst)
        {
            foreach (var kv in inst) target[kv.Key] = kv.Value;
        }
        target["childSettled"] = true;
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
