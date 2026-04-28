using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class ChildToGrandchildSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic gcInput = new ExpandoObject();
        if (data != null)
        {
            if (HasProperty(data, "parentData"))
            {
                gcInput.parentData = data.parentData;
            }
            if (HasProperty(data, "testId"))
            {
                gcInput.testId = data.testId;
            }
            if (HasProperty(data, "childStarted"))
            {
                gcInput.childStarted = data.childStarted;
            }
        }
        LogInformation("ChildToGrandchildSubFlowMapping: prepared grandchild input");
        return Task.FromResult(new ScriptResponse { Data = gcInput });
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
        target["grandchildCompleted"] = true;
        LogInformation("ChildToGrandchildSubFlowMapping: merged grandchild output");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
