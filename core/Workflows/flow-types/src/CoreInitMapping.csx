using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class CoreInitMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.flowType = "C";
        result.coreInitialized = true;
        result.initAt = DateTime.UtcNow.ToString("o");

        LogInformation("CoreInitMapping completed - Core flow type test");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
