using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

/// <summary>
/// Writes what the event carried into instance data, so the test can assert that the transition ran
/// with the mapped body rather than merely that the state moved.
/// </summary>
public class RecordApprovalMapping : IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        dynamic body = new ExpandoObject();
        body.approvals = 1;
        body.decision = context.Body?.decision;
        body.approvedBy = context.Body?.approvedBy;

        return await Task.FromResult(new ScriptResponse { Data = body });
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
        => await Task.FromResult(new ScriptResponse { Data = context.Body });
}
