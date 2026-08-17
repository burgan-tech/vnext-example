using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// B -> C subflow giris/cikis mapping'i.
/// </summary>
public class MiddleToLeafSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic subInput = new ExpandoObject();
        if (data != null && HasProperty(data, "testId"))
        {
            subInput.testId = data.testId;
        }
        LogInformation("MiddleToLeafSubFlowMapping: prepared sub input");
        return Task.FromResult(new ScriptResponse { Data = subInput });
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

        target["leafCompleted"] = true;
        LogInformation("MiddleToLeafSubFlowMapping: merged sub output");
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
