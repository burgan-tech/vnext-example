using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ParentSharedUpdateMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var inst = context.Instance.Data as IDictionary<string, object>;
        if (inst != null)
        {
            foreach (var kv in inst)
            {
                target[kv.Key] = kv.Value;
            }
        }
        target["sharedUpdateExecuted"] = true;
        LogInformation("ParentSharedUpdateMapping: merged instance data with sharedUpdateExecuted");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
