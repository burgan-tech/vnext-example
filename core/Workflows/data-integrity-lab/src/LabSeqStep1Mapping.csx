using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Sequential step 1: merges current data and stamps seq1. Immediate-persist model:
/// this row is on disk before step 2 starts.
/// </summary>
public class LabSeqStep1Mapping : ScriptBase, IMapping
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
        target["seq1"] = true;
        target["seq1At"] = DateTime.UtcNow.ToString("o");
        LogInformation("LabSeqStep1Mapping: seq1 stamped");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
