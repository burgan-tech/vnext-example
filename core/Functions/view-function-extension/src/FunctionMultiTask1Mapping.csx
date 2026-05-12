using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class FunctionMultiTask1Mapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.step = 1;
        result.vfeScript = true;
        result.at = DateTime.UtcNow.ToString("o");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
