using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// caller-roles-lab: the output mapping records whether the CHILD SETTLER — the caller whose request completed
/// the child, carried on the terminal event — holds <c>rule-schema.approver</c>. Null when the child settled
/// without a caller (a timer).
/// </summary>
public class CallerAwareSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        childInput.fromParent = true;
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic output = new ExpandoObject();
        output.settledByApprover = context.Caller.IsAvailable
            ? (object)await context.Caller.HasRoleAsync("rule-schema.approver")
            : null;
        return new ScriptResponse { Data = output };
    }
}
