using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class V2CompletedMapping : ScriptBase, IMapping
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
        if (HasProperty(data, "initialized"))
            result.initialized = data.initialized;
        if (HasProperty(data, "initAt"))
            result.initAt = data.initAt;
        if (HasProperty(data, "reviewExecuted"))
            result.reviewExecuted = data.reviewExecuted;
        if (HasProperty(data, "reviewAt"))
            result.reviewAt = data.reviewAt;

        result.v2Completed = true;
        result.completedAt = DateTime.UtcNow.ToString("o");
        result.completedByVersion = "2.0.0";

        LogInformation("V2CompletedMapping completed - v2 path finalized");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
