using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class V1CompletedMapping : ScriptBase, IMapping
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

        result.v1Completed = true;
        result.completedAt = DateTime.UtcNow.ToString("o");
        result.completedByVersion = "1.0.0";

        LogInformation("V1CompletedMapping completed - v1 path finalized");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
