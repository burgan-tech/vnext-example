using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class InitVersionMapping : ScriptBase, IMapping
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

        result.initialized = true;
        result.initAt = DateTime.UtcNow.ToString("o");

        LogInformation("InitVersionMapping completed - version consistency test initialized");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
