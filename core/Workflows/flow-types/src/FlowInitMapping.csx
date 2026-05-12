using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class FlowInitMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.flowType = "F";
        result.flowInitialized = true;
        result.initAt = DateTime.UtcNow.ToString("o");

        LogInformation("FlowInitMapping completed - Flow type test");

        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
