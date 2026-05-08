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
        if (HasProperty(data, "startedAt")) result.startedAt = data.startedAt;
        if (HasProperty(data, "retryHandled")) result.retryHandled = data.retryHandled;
        result.errorIgnored = true;
        LogInformation("IgnoreErrorMapping: error was ignored, continuation confirmed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
