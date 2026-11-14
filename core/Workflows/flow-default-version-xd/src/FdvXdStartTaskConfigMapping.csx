using System;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// onEntry of st-initial: start partner/fdv-xd-contract through a Start task (type 11) over Dapr. Domain,
/// flow, version ("1.2.0"), useDapr and sync come ONLY from the task's config — this mapping must not call
/// SetVersion, otherwise it would hide whether the config version reaches the partner. Only the key is set,
/// so the test can read the partner instance.
/// </summary>
public class FdvXdStartTaskConfigMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var start = task as StartTask ?? throw new InvalidOperationException("Task must be a StartTask");

        start.SetKey($"fdv-xd-st-{context.Instance?.Id}");
        start.SetBody(new { parentInstanceId = context.Instance?.Id, parentDomain = "core" });

        return Task.FromResult(new ScriptResponse { Data = context.Instance?.Data });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }
}
