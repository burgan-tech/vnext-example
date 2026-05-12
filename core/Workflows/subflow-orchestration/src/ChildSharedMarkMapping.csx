using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ChildSharedMarkMapping : ScriptBase, IMapping
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
        if (HasProperty(data, "parentData")) result.parentData = data.parentData;
        if (HasProperty(data, "testId")) result.testId = data.testId;
        result.childSharedMarkExecuted = true;
        result.childSharedMarkAt = DateTime.UtcNow.ToString("o");
        LogInformation("ChildSharedMarkMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
