using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class InitExtendedTaskMapping : ScriptBase, IMapping
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

        result.initCompleted = true;
        result.initAt = DateTime.UtcNow.ToString("o");
        result.taskResults = new ExpandoObject();

        LogInformation("InitExtendedTaskMapping completed");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
