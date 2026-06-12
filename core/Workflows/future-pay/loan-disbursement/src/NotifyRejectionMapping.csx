using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T5 (reject) order 1 — Notification Task (type 10).
/// Records the rejection onto the master `approval` section and notifies the customer.
/// </summary>
public class NotifyRejectionMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var body = context.Body;
        var data = context.Instance?.Data;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                approval = new
                {
                    decision = "rejected",
                    decisionReason = body?.rejectionReason
                },
                rejectionCode = body?.rejectionCode,
                recipient = data?.application?.customerId,
                channel = "email",
                template = "loan-rejected"
            },
            Tags = new[] { "notification", "rejection" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse
        {
            Data = new { notified = context.Body?.isSuccess ?? true }
        });
    }
}
