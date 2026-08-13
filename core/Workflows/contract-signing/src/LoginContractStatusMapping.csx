using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

// onExecution of login-flow's updateData transition (contract-status).
// contract-flow calls this once, just before completing, to hand the parent a
// "contract finished" flag. The transition payload is merged into instance data at the
// root, so contractCompleted arrives there; we default to true when the caller sends
// no body, because the only reason to invoke this transition is to report completion.
// Return ONLY the delta: writing back a full snapshot would clobber concurrent writes.
public class LoginContractStatusMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance.Data as IDictionary<string, object>;
        bool completed = true;
        if (data != null && data.TryGetValue("contractCompleted", out var v) && v != null)
        {
            if (!bool.TryParse(v.ToString(), out completed)) completed = true;
        }
        LogInformation($"LoginContractStatusMapping: contractCompleted={completed}");
        return Task.FromResult(new ScriptResponse { Data = new { contractCompleted = completed } });
    }
}
