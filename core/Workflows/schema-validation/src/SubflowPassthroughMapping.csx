using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class SubflowPassthroughMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic childInput = new ExpandoObject();
        if (data != null && HasProperty(data, "orderId"))
        {
            childInput.orderId = data.orderId;
        }
        LogInformation("SubflowPassthroughMapping: prepared child input");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
