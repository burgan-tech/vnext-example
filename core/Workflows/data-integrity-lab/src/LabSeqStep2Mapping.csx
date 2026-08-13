using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Sequential step 2 — ordering probe: records whether step 1's output (seq1) is visible
/// in the snapshot. Under the immediate-persist model every earlier sequential task's row
/// is persisted AND reflected into the ScriptContext snapshot before the next task runs,
/// so seq1SeenBySeq2 must always be true.
/// </summary>
public class LabSeqStep2Mapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        // Delta-only: read the snapshot for the probe, but return ONLY our own keys.
        var data = context.Instance.Data as IDictionary<string, object>;
        var seq1Seen = data != null && data.ContainsKey("seq1");

        target["seq2"] = true;
        target["seq2At"] = DateTime.UtcNow.ToString("o");
        target["seq1SeenBySeq2"] = seq1Seen;
        LogInformation($"LabSeqStep2Mapping: seq2 stamped, seq1Seen={seq1Seen}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
