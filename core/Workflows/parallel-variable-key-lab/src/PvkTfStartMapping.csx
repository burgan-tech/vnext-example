using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Start (task 11), same domain: starts a fresh pvk-tf-target under a new key, tagged with the run's
/// startedBy marker (not testId, so the order-1 lists stay pinned to the three seeds).
/// </summary>
public class PvkTfStartMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var start = task as StartTask ?? throw new InvalidOperationException("Task must be a StartTask");
        start.SetKey(Guid.NewGuid().ToString("N"));
        start.SetBody(new { startedBy = context.Instance.Id.ToString() });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new ExpandoObject() });
}
