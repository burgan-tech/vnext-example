using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class InitCollectionTestMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        LogInformation("CollectionObjectTest - Workflow initialized");

        dynamic result = new ExpandoObject();
        result.testId = Guid.NewGuid().ToString();
        result.startedAt = DateTime.UtcNow.ToString("o");

        return Task.FromResult(new ScriptResponse
        {
            Data = result
        });
    }
}
