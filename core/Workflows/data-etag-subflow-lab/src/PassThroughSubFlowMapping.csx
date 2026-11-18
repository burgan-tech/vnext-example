using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

// The child starts empty; the lab measures what the parent's data function serves while the child
// runs, not what flows between the two.
public class PassThroughSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        dynamic input = new ExpandoObject();
        input.attempt = 0;
        return Task.FromResult(new ScriptResponse { Data = input });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
        => Task.FromResult(new ScriptResponse());
}
