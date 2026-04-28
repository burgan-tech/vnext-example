using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class InitErrorTestMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.testId = Guid.NewGuid().ToString();
        result.errorTestStarted = true;
        result.startedAt = DateTime.UtcNow.ToString("o");
        LogInformation("InitErrorTestMapping: error boundary integration test initialized");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
