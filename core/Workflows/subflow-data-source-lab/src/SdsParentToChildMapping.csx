using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Input: hands the parent's marker to the child so the child's data also shows where it came from.
// Output: merges the child's result into the parent and stamps `childResult`.
public class SdsParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic childInput = new ExpandoObject();
        if (data != null && HasProperty(data, "parentMarker"))
        {
            childInput.parentMarker = data.parentMarker;
        }
        if (data != null && HasProperty(data, "labRef"))
        {
            childInput.labRef = data.labRef;
        }
        LogInformation("SdsParentToChildMapping: prepared child input");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var body = context.Body as IDictionary<string, object>;
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
        if (body != null)
        {
            foreach (var kv in body)
            {
                target[kv.Key] = kv.Value;
            }
        }
        target["childResult"] = true;
        LogInformation("SdsParentToChildMapping: merged child output");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
