using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class InitVfeMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.vfeTestStarted = true;
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
