using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T7 (execute-disbursement) order 2 — HTTP Task (type 6), MockLab.
/// Transfers the approved loan amount to the customer's account and records the disbursement
/// detail (disbursedAmount, accountNumber, transactionRef, disbursementDate) on the master section.
/// </summary>
public class TransferToAccountMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var httpTask = task as HttpTask;
        if (httpTask == null)
            throw new InvalidOperationException("Task must be an HttpTask");

        var data = context.Instance?.Data;
        httpTask.SetBody(new
        {
            customerId = data?.application?.customerId,
            amount = data?.assessment?.approvedLimit,
            currency = data?.application?.currency
        });
        httpTask.SetHeaders(new Dictionary<string, string?>
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        });

        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var payload = context.Body?.data ?? context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                disbursement = new
                {
                    disbursedAmount = payload?.disbursedAmount,
                    accountNumber = payload?.accountNumber,
                    transactionRef = payload?.transactionRef,
                    disbursementDate = DateTime.UtcNow.ToString("o")
                }
            },
            Tags = new[] { "disbursement", "transfer" }
        });
    }
}
