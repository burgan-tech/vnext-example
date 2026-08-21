using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// The other half of the single-write instrument. Runs as onEntry order 2 of
/// <c>documents-processing</c> — the very next task after the fan-out batch, inside the SAME
/// state entry, with no transition, no state change and no other task in between.
/// <para>
/// That placement is deliberate and load-bearing. It reads
/// <c>context.Instance.LatestData.Version</c> before applying its own output, so the value it
/// stamps is exactly the version the fan-out batch produced. The test then asserts
/// <c>patch(versionAfterFanOut) - patch(versionSeenByFanOut) == 1</c>. Move this task onto a
/// transition or into another state and any intervening write silently widens that delta,
/// turning the scenario's most important assertion into noise.
/// </para>
/// <para>
/// Under the immediate-persist InstanceData model every earlier task's row is on disk AND
/// reflected into the ScriptContext snapshot before the next task runs, so the read here is of
/// the fan-out's committed row, not a stale one (the same guarantee data-integrity-lab pins with
/// <c>seq1SeenBySeq2</c>).
/// </para>
/// </summary>
public class FanOutStampAfterMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;

        var version = context.Instance?.LatestData?.Version ?? string.Empty;

        target["versionAfterFanOut"] = version;
        target["batchSettledAtUtc"] = DateTime.UtcNow.ToString("o");

        LogInformation($"FanOutStampAfterMapping: versionAfterFanOut={version}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
