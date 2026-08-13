using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

public class ParentStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data;
        dynamic result = new ExpandoObject();
        result.parentStarted = true;
        if (data != null && HasProperty(data, "testId"))
        {
            result.testId = data.testId;
        }

        // updateData concurrency probe: seed the fan-in counter and carry the
        // caller-provided threshold (default 5) for the parent-collect gate.
        result.updateCount = 0;
        result.updateThreshold = 5;
        if (data != null && HasProperty(data, "updateThreshold"))
        {
            result.updateThreshold = data.updateThreshold;
        }

        LogInformation("ParentStartMapping: parentStarted set");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
