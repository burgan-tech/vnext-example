using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Retrieves a single workflow instance by the "oldKey" value sent in the transition body.
/// </summary>
public class GetInstanceByOldKeyMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var instanceTask = (task as GetInstanceDataTask)!;

        string? oldKey = context.Body?.oldKey?.ToString();

        instanceTask.SetInstance(oldKey);

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Key = "get-instance-by-old-key-result",
            Data = new
            {
                instance = context.Body,
                fetchedAt = System.DateTime.UtcNow
            }
        });
    }
}
