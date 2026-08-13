using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// DataHash dedup probe: re-stamps keys that already carry exactly these values
/// (an idempotent duplicate callback). The write service merges this delta into the head,
/// computes the merged content's hash, finds it equal to the head's DataHash and must NOT
/// create a new version row. The test asserts the sequential transition produced exactly
/// 3 rows, not 4.
/// </summary>
public class LabDupEchoMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["seq1"] = true;
        target["seq3"] = true;
        target["labStarted"] = true;

        LogInformation("LabDupEchoMapping: re-stamping already-set keys (expect dedup, no new version)");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
