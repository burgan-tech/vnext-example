using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class InitTaskTestMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.testId = Guid.NewGuid().ToString();
        result.startedAt = DateTime.UtcNow.ToString("o");
        LogInformation("InitTaskTestMapping: task execution test initialized");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
