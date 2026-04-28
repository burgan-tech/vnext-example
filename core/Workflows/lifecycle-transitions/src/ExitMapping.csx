using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class ExitMapping : ScriptBase, IMapping
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
        if (HasProperty(data, "initializeCompleted"))
            result.initializeCompleted = data.initializeCompleted;

        result.exitExecuted = true;
        result.exitAt = DateTime.UtcNow.ToString("o");

        LogInformation("ExitMapping completed - workflow exited via exit transition");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
