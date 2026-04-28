using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class SubProcessInitMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.flowType = "P";
        result.subProcessInitialized = true;
        result.initAt = DateTime.UtcNow.ToString("o");

        LogInformation("SubProcessInitMapping completed - SubProcess flow type test");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
