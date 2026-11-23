using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// start-rollback-lab: the parent's SubFlow input. It passes <c>customerId</c> on only when the
/// parent's own data has one, so a parent started without it starts a child whose start mapping
/// throws (<c>RequireCustomerStartMapping</c>).
/// </summary>
public class ParentToChildMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic childInput = new ExpandoObject();
        if (data != null && HasProperty(data, "runId"))
            childInput.runId = data.runId;
        if (data != null && HasProperty(data, "customerId"))
            childInput.customerId = data.customerId;

        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    // The child never completes in this lab; nothing comes back.
    public Task<ScriptResponse> OutputHandler(ScriptContext context) => Task.FromResult(new ScriptResponse());
}
