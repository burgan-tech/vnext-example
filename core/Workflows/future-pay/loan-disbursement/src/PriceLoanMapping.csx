using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T3 (submit-assessment) order 2 — Script Task (type 7).
/// Derives pricing (interest rate, insurance premium, monthly installment, APR) and merges
/// the assessor-confirmed pricing fields into the master `pricing` section.
/// </summary>
public class PriceLoanMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var body = context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                pricing = new
                {
                    interestRate = body?.interestRate,
                    insurancePremium = body?.insurancePremium,
                    monthlyInstallment = body?.monthlyInstallment,
                    apr = body?.apr
                }
            },
            Tags = new[] { "pricing" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = context.Body });
    }
}
