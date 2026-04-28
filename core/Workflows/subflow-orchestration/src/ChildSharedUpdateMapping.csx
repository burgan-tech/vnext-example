using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ChildSharedUpdateMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        if (HasProperty(data, "childStarted")) result.childStarted = data.childStarted;
        if (HasProperty(data, "grandchildCompleted")) result.grandchildCompleted = data.grandchildCompleted;
        result.childSharedUpdateExecuted = true;
        result.childSharedUpdateAt = DateTime.UtcNow.ToString("o");
        LogInformation("ChildSharedUpdateMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
