using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start task for schedule-after-auto: normalises the caller's <c>mode</c> into instance data and
/// zeroes the two probe counters.
/// <para>
/// The gate's automatic transition is decided by <c>mode</c>, and the auto step (LifecycleOrder.Auto)
/// runs inside THIS pipeline — after this task, after ChangeState. Writing the flag here rather than
/// relying on the request payload alone makes the rule read a persisted value, so the two paths are
/// deterministic instead of depending on payload overlay timing.
/// </para>
/// <para>
/// DELTA-ONLY: returns just the keys it owns. Full-echo would overwrite concurrent writers with a
/// stale snapshot; the merge keeps head anyway.
/// </para>
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

        // "park" is the safe default: an unrecognised payload must NOT silently take the
        // auto-winner path, or a broken test would look green for the wrong reason.
        var mode = "park";
        if (data != null && data.TryGetValue("mode", out var rawMode) && rawMode != null)
        {
            mode = rawMode.ToString();
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["mode"] = mode;
        target["autoAdvances"] = 0;
        target["timeoutFired"] = 0;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {
            target["testId"] = testId.ToString();
        }

        LogInformation($"SeedMapping: mode={mode}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
