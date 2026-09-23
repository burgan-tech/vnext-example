using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Task 14 (SubProcess) mapping for ht-a-spawn-process -> partner/ht-d, fire-and-forget over Dapr
/// service invocation. It gets its OWN hop budget, because it is not a continuation of the root's
/// chain — it is a separate unit of work that may itself descend through SubFlows.
/// </summary>
/// <remarks>
/// A state can no longer start a SubProcess directly (<c>state.subFlow.type: "P"</c> is rejected at
/// publish by the runtime's validator — a state starts a SubFlow and only a SubFlow). SubProcess
/// start is now expressed as this <c>SubProcessTask</c> (type "14") wired on the transition's
/// <c>onExecutionTasks</c>, which is why this mapping implements <c>IMapping</c> and casts its task
/// to <c>SubProcessTask</c> instead of implementing <c>ISubProcessMapping</c> against a
/// <c>subFlow.process</c> block.
/// </remarks>
public class SpawnProcessMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var sub = task as SubProcessTask ?? throw new InvalidOperationException("Task must be a SubProcessTask");
        var data = context.Instance?.Data as IDictionary<string, object>;

        var hops = 0;
        if (data != null && data.TryGetValue("processHops", out var raw) && raw != null)
        {
            int.TryParse(raw.ToString(), out hops);
        }

        dynamic body = new ExpandoObject();
        body.hops = hops;

        if (data != null && data.TryGetValue("testId", out var testId) && testId != null)
        {
            body.testId = testId;
        }

        dynamic humanTask = new ExpandoObject();
        humanTask.title = "HT-D process step";
        humanTask.description = "HT-D process step description";
        body.humanTask = humanTask;

        sub.SetDomain("partner");
        sub.SetFlow("ht-d");
        sub.SetVersion("1.0.6");
        sub.SetUseDapr(true);
        sub.SetSync(false);
        sub.SetBody(body);

        LogInformation($"SpawnProcessMapping: spawning ht-d as SubProcess with hops={hops}");
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        // Fire-and-forget: the SubProcess is an independent unit of work and nothing projects its
        // state upward, so there is nothing to merge back into ht-a's own data.
        return Task.FromResult(new ScriptResponse());
    }
}
