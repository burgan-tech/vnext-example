using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class NotifyMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();

        if (HasProperty(data, "testId"))
            result.testId = data.testId;
        if (HasProperty(data, "retryCompleted"))
            result.retryCompleted = data.retryCompleted;
        if (HasProperty(data, "errorIgnored"))
            result.errorIgnored = data.errorIgnored;
        if (HasProperty(data, "rollbackExecuted"))
            result.rollbackExecuted = data.rollbackExecuted;
        if (HasProperty(data, "rollbackAt"))
            result.rollbackAt = data.rollbackAt;
        if (HasProperty(data, "logExecuted"))
            result.logExecuted = data.logExecuted;

        result.notifyExecuted = true;
        result.notifyAt = DateTime.UtcNow.ToString("o");

        LogInformation("NotifyMapping completed - notify action test");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
