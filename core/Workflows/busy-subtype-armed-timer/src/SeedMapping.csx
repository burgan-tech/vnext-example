using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start task for busy-subtype-armed-timer: zeroes the probe counters, starts the gate closed and
/// records how long the wait state's timer should be (<c>timerSeconds</c>, default 20).
/// <para>
/// The duration comes from the caller so one flow serves both sides of the scenario: a long timer
/// for "the updateData opens the gate long before the timer is due", a short one for "nothing
/// arrives and the timer fires". DELTA-ONLY: returns just the keys it owns.
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

        var timerSeconds = 20;
        if (data != null && data.TryGetValue("timerSeconds", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out timerSeconds);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["timerSeconds"] = timerSeconds;
        target["contractReady"] = false;
        target["readyAdvances"] = 0;
        target["timeoutFired"] = 0;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {
            target["testId"] = testId.ToString();
        }

        LogInformation($"SeedMapping: timerSeconds={timerSeconds}");
        return Task.FromResult(new ScriptResponse { Data = result });
    }
}
