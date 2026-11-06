using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Holds the child's start open: while this runs, the parent's correlation already points at the
// child but the child's row does not exist yet (vnext#1181). Nothing else differs from the
// pass-through mapping.
public class ChainSlowSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public async Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        await Task.Delay(6000);
        dynamic input = new ExpandoObject();
        input.startedByChainLab = true;
        return new ScriptResponse { Data = input };
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse());
}
