using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// Pass-through: this lab measures authorization, not data flow. The child only needs to
// start; nothing downstream reads what it was started with.
public class ChainSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.startedByChainLab = true;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse());
}
