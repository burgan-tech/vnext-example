using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onExecute of the gate's automatic transition. Counts the hop and — deliberately — takes a
/// couple of seconds doing it.
/// <para>
/// <b>Why the delay is load-bearing.</b> The chained hop runs CancelScheduledJobs (order 39) right
/// after this task (order 30). A runtime that armed the gate's timer before evaluating the auto
/// condition tears that timer down in the very next hop, so at rest BOTH orderings show no armed
/// job — the resting state cannot tell them apart. The delay holds the chained hop open long
/// enough for a poller to see the armed entry if it was ever created, which is what makes
/// "no scheduled entry was EVER observed" a discriminating observation rather than a tautology.
/// </para>
/// <para>Kept well under the gate timer's duration so the timer cannot fire inside the window.</para>
/// </summary>
public class AutoAdvanceMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        var data = context.Instance.Data as IDictionary<string, object>;

        var current = 0;
        if (data != null && data.TryGetValue("autoAdvances", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out current);
        }

        dynamic result = new ExpandoObject();
        var target = (IDictionary<string, object>)result;
        target["autoAdvances"] = current + 1;

        LogInformation($"AutoAdvanceMapping: autoAdvances {current} -> {current + 1}");
        return new ScriptResponse { Data = result };
    }
}
