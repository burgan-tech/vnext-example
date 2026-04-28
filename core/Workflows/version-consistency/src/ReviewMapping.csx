using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ReviewMapping : ScriptBase, IMapping
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

        result.reviewExecuted = true;
        result.reviewAt = DateTime.UtcNow.ToString("o");

        LogInformation("ReviewMapping completed - v2 review state entered");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
