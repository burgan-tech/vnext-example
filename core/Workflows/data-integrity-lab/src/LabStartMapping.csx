using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start task for data-integrity-lab: seeds the counter and carries the caller-provided
/// threshold (default 4). One data row is expected from this task (version 1.0.0).
/// </summary>
public class LabStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        var data = context.Instance.Data as IDictionary<string, object>;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {
            target["testId"] = testId.ToString();
        }

        var threshold = 4;
        if (data != null && data.TryGetValue("labThreshold", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out threshold);
        }

        target["labStarted"] = true;
        target["labUpdateCount"] = 0;
        target["labThreshold"] = threshold;

        LogInformation($"LabStartMapping: seeded labThreshold={threshold}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
