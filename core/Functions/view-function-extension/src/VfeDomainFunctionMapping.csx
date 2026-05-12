using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

public class VfeDomainFunctionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        result.functionScope = "D";
        result.domainFunction = true;
        result.executedAt = DateTime.UtcNow.ToString("o");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
