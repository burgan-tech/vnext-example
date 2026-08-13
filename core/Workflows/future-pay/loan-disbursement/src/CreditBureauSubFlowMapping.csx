using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

///<summary>
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
                { "customerId", data?.customerId?.ToString() ?? string.Empty }
            }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        dynamic result = context.Body;
        var creditBureau = CreateObject();
        SetProperty(creditBureau, "kkbScore", result?.kkbScore);
        SetProperty(creditBureau, "findeksNote", result?.findeksNote);
        SetProperty(creditBureau, "totalExistingDebt", result?.totalExistingDebt);
        SetProperty(creditBureau, "inquiryDate", result?.inquiryDate);

        var data = CreateObject();
        SetProperty(data, "creditBureau", creditBureau);

        return Task.FromResult(new ScriptResponse { Data = data, Tags = new[] { "subflow", "credit-bureau" } });
    }
}
