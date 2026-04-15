using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class OnEntryGetInstanceDataMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var triggerTask = (task as GetInstanceDataTask)!;

        triggerTask.SetInstance(context.Instance.Key);

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse()
        {
            Data = new
            {
                instanceSnapshot = context.Body,
                fetchedAt = System.DateTime.UtcNow
            }
        });
    }
}
