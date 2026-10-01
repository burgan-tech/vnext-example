using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// SubFlow mapping for the fault chain: hands <c>testId</c> down so every level of one run can be
/// correlated, and copies nothing back — a faulted child's output is not what the chain asserts.
/// </summary>
public class EbSubFlowPassMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        var data = context.Instance?.Data as IDictionary<string, object>;
        if (data != null && data.TryGetValue("testId", out var testId))
        {
            childInput.testId = testId;
        }

        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
