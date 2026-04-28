using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class IgnoreErrorMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        if (HasProperty(data, "testId")) result.testId = data.testId;
        if (HasProperty(data, "errorTestStarted")) result.errorTestStarted = data.errorTestStarted;
        if (HasProperty(data, "httpErrorHandled")) result.httpErrorHandled = data.httpErrorHandled;
        result.errorIgnored = true;
        LogInformation("IgnoreErrorMapping: continuation after ignore policy");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
