using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Sequential step 3: merges current data and stamps seq3.
/// </summary>
public class LabSeqStep3Mapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Delta-only by design: the write service merges this into the DB head under the
        // row lock, so echoing existing data is not only unnecessary — under concurrent
        // writers a stale echoed value would overwrite a fresher one.
        target["seq3"] = true;
        target["seq3At"] = DateTime.UtcNow.ToString("o");
        LogInformation("LabSeqStep3Mapping: seq3 stamped");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
