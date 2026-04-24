using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Filters workflow instances by the "oldKey" value sent in the transition body.
/// </summary>
public class GetInstancesByOldKeyMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var instancesTask = (task as GetInstancesTask)!;

        string? oldKey = context.Body?.oldKey?.ToString();

        instancesTask.SetFilter(new
        {
            key = new { eq = oldKey }
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Key = "get-instances-by-old-key-result",
            Data = new
            {
                instances = context.Body,
                fetchedAt = System.DateTime.UtcNow
            }
        });
    }
}
