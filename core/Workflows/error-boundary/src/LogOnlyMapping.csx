using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class LogOnlyMapping : ScriptBase, IMapping
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
        if (HasProperty(data, "errorIgnored")) result.errorIgnored = data.errorIgnored;
        if (HasProperty(data, "rollbackExecuted")) result.rollbackExecuted = data.rollbackExecuted;
        if (HasProperty(data, "rollbackAt")) result.rollbackAt = data.rollbackAt;
        result.logOnlyExecuted = true;
        LogInformation("LogOnlyMapping: log-only path executed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
