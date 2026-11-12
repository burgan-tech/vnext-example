using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>rule-schema-lab-echo: answers <c>{ "accepted": true }</c> once input validation passed.</summary>
public class EchoMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic payload = new ExpandoObject();
        payload.accepted = true;
        dynamic response = new ExpandoObject();
        ((IDictionary<string, object>)response)["data"] = payload;
        return Task.FromResult(new ScriptResponse { Data = response });
    }
}
