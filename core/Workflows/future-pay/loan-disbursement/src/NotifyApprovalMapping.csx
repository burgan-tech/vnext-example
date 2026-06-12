using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T4 (approve) order 1 — Notification Task (type 10).
/// Records the approval decision onto the master `approval` section and notifies the customer.
/// </summary>
public class NotifyApprovalMapping : ScriptBase, IMapping
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
                    decision = "approved",
                    decisionReason = body?.decisionReason,
                    conditions = body?.conditions,
                    approverUserId = body?.approverUserId
                },
                recipient = data?.application?.customerId,
                channel = "email",
                template = "loan-approved"
            },
            Tags = new[] { "notification", "approval" }
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
