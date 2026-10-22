using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>
/// SubFlow mapping core/fdv-xd-onboarding* → partner/fdv-xd-contract. The child needs only to be
/// started; WHICH version starts is decided on the partner side (its sys-version-registry entry).
/// Signature note: InputHandler(ScriptContext) — a wrong signature silently keeps the child from starting.
/// </summary>
public class FdvXdContractSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic childInput = new ExpandoObject();
        childInput.parentInstanceId = context.Instance?.Id;
        childInput.parentDomain = "core";
        return Task.FromResult(new ScriptResponse { Data = childInput });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
