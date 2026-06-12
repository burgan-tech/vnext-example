using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// State 5 (collateral-establishment, stateType 4) SubFlow mapping.
/// Seeds the collateral subflow from the application/approval context and, on completion,
/// merges the collateral detail (type, value, status) into the master `collateral` section.
/// </summary>
public class CollateralSubFlowMapping : ScriptBase, ISubFlowMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data;
        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object>
            {
                { "customerId", data?.application?.customerId?.ToString() ?? string.Empty },
                { "approvedLimit", data?.assessment?.approvedLimit ?? (object)0 },
                { "productType", data?.application?.productType?.ToString() ?? string.Empty }
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
                collateral = new
                {
                    collateralType = result?.collateralType,
                    collateralValue = result?.collateralValue,
                    establishmentStatus = result?.establishmentStatus
                }
            },
            Tags = new[] { "subflow", "collateral" }
        });
    }
}
