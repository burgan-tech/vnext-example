using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// Parent Initial - Get Instance Data Mapping
/// Fetches instance data from account-opening workflow on entry of parent-initial state.
/// </summary>
public class ParentInitialGetInstanceDataMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var triggerTask = (task as GetInstanceDataTask)!;

        triggerTask.SetInstance("50044086189");

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse()
        {
            Data = new
            {
                accountOpeningSnapshot = context.Body,
                fetchedAt = System.DateTime.UtcNow
            }
        });
    }
}
