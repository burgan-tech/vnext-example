using System;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// start-rollback-lab: the SubProcess parent's task input. It starts <c>start-rollback-lab-process</c>
/// and passes <c>customerId</c> on only when the parent's own data has one, so a parent started
/// without it starts a process whose start mapping throws (<c>RequireCustomerStartMapping</c>).
/// </summary>
public class ParentToProcessMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var subProcess = task as SubProcessTask
            ?? throw new InvalidOperationException("start-rollback-lab: the task must be a SubProcessTask");

        var data = context.Instance.Data;
        dynamic body = new ExpandoObject();
        if (data != null && HasProperty(data, "runId"))
            body.runId = data.runId;
        if (data != null && HasProperty(data, "customerId"))
            body.customerId = data.customerId;

        subProcess.SetBody(body);
        subProcess.SetSync(false);
        return Task.FromResult(new ScriptResponse());
    }

    // Fire and forget: the process reports nothing back to the parent.
    public Task<ScriptResponse> OutputHandler(ScriptContext context) => Task.FromResult(new ScriptResponse());
}
