using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T1 (submit-application) order 2 — Notification Task (type 10).
/// Notifies the customer that the application was received.
/// </summary>
public class NotifyApplicationReceivedMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance?.Data;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                recipient = data?.application?.customerId,
                channel = "email",
                template = "loan-application-received",
                parameters = new
                {
                    applicationId = data?.application?.applicationId,
                    requestedAmount = data?.application?.requestedAmount,
                    currency = data?.application?.currency
                }
            },
            Tags = new[] { "notification", "application-received" }
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
