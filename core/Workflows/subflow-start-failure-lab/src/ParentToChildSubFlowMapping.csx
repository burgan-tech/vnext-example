using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// Input mapping for the parent-subflow-state → child SubFlow start.
/// <para>
/// Deliberately never supplies <c>mustProvide</c>, the field
/// <c>subflow-start-failure-lab-child-start</c>'s schema requires. HandleSubFlowStep (order 70)
/// commits the parent's InstanceCorrelation in its own unit of work before the post-commit
/// StartSubflowJob ever calls the child's start transition, so by the time that transition fails
/// schema validation, the correlation is already durable and the child was never created.
/// </para>
/// </summary>
public class ParentToChildSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic childInput = new ExpandoObject();
        if (data != null && HasProperty(data, "testId"))
        {
            childInput.testId = data.testId;
        }
        // "mustProvide" is intentionally NOT set here — see class summary.
        LogInformation("ParentToChildSubFlowMapping: prepared child input (mustProvide omitted on purpose)");
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Never invoked: the child never starts, so the subflow correlation never completes.
        return Task.FromResult(new ScriptResponse());
    }
}
