using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class RollbackMapping : ScriptBase, IMapping
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

        result.rollbackExecuted = true;
        result.rollbackAt = DateTime.UtcNow.ToString("o");

        LogInformation("RollbackMapping completed - rollback action test");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
