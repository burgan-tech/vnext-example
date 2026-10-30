using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start task for auto-priority-lab: normalises the caller's <c>skipHigh</c> into instance data.
/// <para>
/// The automatic transitions of <c>evaluate</c> run inside THIS pipeline, after this task. Writing
/// the flag here makes <c>HighPriorityRule</c> read a persisted value instead of depending on
/// request-payload overlay timing. Same approach as schedule-after-auto.
/// </para>
/// <para>DELTA-ONLY: returns just the key it owns.</para>
/// </summary>
public class SeedMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;

        // false is the safe default: an unrecognised payload must NOT skip the high-priority rule.
        var skipHigh = false;
        if (data != null && data.TryGetValue("skipHigh", out var raw) && raw != null)
        {
            bool.TryParse(raw.ToString(), out skipHigh);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["skipHigh"] = skipHigh;

        LogInformation($"SeedMapping: skipHigh={skipHigh}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
