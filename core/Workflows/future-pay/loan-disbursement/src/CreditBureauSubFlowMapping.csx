using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// State 2 (credit-bureau-inquiry, stateType 4) SubFlow mapping.
/// Passes customerId/application into the credit-bureau-inquiry subflow; on completion,
/// merges the bureau result (kkbScore, findeksNote, totalExistingDebt, inquiryDate) into
/// the master `creditBureau` section.
/// </summary>
public class CreditBureauSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data;
        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object>
            {
                { "customerId", data?.application?.customerId?.ToString() ?? string.Empty },
                { "application", data?.application ?? (object)new { } }
            }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var result = context.Body?.data ?? context.Body?.attributes ?? context.Body;
        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                creditBureau = new
                {
                    kkbScore = result?.kkbScore,
                    findeksNote = result?.findeksNote,
                    totalExistingDebt = result?.totalExistingDebt,
                    inquiryDate = result?.inquiryDate
                }
            },
            Tags = new[] { "subflow", "credit-bureau" }
        });
    }
}
