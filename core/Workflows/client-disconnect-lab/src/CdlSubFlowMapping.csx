using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// SubFlow mapping for the client-disconnect lab's parent → child pair. The scenario is about the
/// forwarded transition's run surviving a client abort, not about data, so the input carries only a
/// marker and the output copies the parent's data plus <c>childSettled</c>.
/// </summary>
/// <remarks>
/// It exists because <c>subFlow.mapping</c> is required by the schema. A mapping that moved real data
/// would give a failing test a second possible cause.
/// </remarks>
public class CdlSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        childInput.startedByClientDisconnectLabParent = true;
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic merged = new ExpandoObject();
        var target = (IDictionary<string, object>)merged;
        if (context.Instance.Data is IDictionary<string, object> instanceData)
        {
            foreach (var kv in instanceData)
            {
                target[kv.Key] = kv.Value;
            }
        }

        target["childSettled"] = true;
        return Task.FromResult(new ScriptResponse { Data = merged });
    }
}
