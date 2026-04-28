using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class InitInstanceMgmtMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;

        dynamic result = new ExpandoObject();

        result.category = HasProperty(data, "category") ? (string)data.category : "default";
        result.priority = HasProperty(data, "priority") ? Convert.ToInt32(data.priority) : 1;
        result.testStarted = true;
        result.startedAt = DateTime.UtcNow.ToString("o");

        LogInformation($"InitInstanceMgmtMapping: category={result.category}, priority={result.priority}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
