using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T7 (execute-disbursement) order 3 — Notification Task (type 10).
/// Notifies the customer (SMS) that the loan has been disbursed to their account.
/// </summary>
public class NotifyDisbursedMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var data = context.Instance?.Data;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                recipient = data?.application?.customerId,
                channel = "sms",
                template = "loan-disbursed",
                parameters = new
                {
                    disbursedAmount = data?.disbursement?.disbursedAmount,
                    accountNumber = data?.disbursement?.accountNumber,
                    transactionRef = data?.disbursement?.transactionRef
                }
            },
            Tags = new[] { "notification", "disbursed" }
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
