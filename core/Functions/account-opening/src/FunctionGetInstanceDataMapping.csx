using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Task 2 mapping for multi-task function test.
/// Fetches current workflow instance data via GetInstanceDataTask.
/// </summary>
public class FunctionGetInstanceDataMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var instanceTask = task as GetInstanceDataTask;
        instanceTask?.SetInstance(context.Instance.Key);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Key = "instance-data-result",
            Data = context.Body
        });
    }
}
