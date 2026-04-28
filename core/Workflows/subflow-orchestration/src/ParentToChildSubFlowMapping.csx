using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class ParentToChildSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic childInput = new ExpandoObject();
        if (data != null && HasProperty(data, "parentData"))
        {
            childInput.parentData = data.parentData;
        }
        if (data != null && HasProperty(data, "testId"))
        {
            childInput.testId = data.testId;
        }
        LogInformation("ParentToChildSubFlowMapping: prepared child input");
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
        target["childCompleted"] = true;
        LogInformation("ParentToChildSubFlowMapping: merged child output");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
