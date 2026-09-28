using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Seviye 4 -> 5 subflow mapping'i. Giris: yalniz testId; cikis: parent verisi + alt sonucu.
/// </summary>
public class L4ToL5SubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic subInput = new ExpandoObject();
        if (data != null && HasProperty(data, "testId"))
        {
            subInput.testId = data.testId;
        }
        return Task.FromResult(new ScriptResponse { Data = subInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        target["l5Completed"] = true;
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
