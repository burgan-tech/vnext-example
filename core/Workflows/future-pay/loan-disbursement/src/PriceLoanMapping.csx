using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// T3 (submit-assessment) order 2 — Script Task (type 7).
/// Reads the pricing fields the assessor submitted (already merged into instance data at root
/// by the runtime) and projects them into the master `pricing` section from OutputHandler.
/// Values are passed through unconverted so they keep their original JSON numeric type.
/// </summary>
public class PriceLoanMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Script Task: nothing to configure, nothing to persist.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var existing = Get(data, "pricing") as IDictionary<string, object>;

        var interestRate = Pick(data, existing, "interestRate");
        var insurancePremium = Pick(data, existing, "insurancePremium");
        var monthlyInstallment = Pick(data, existing, "monthlyInstallment");
        var apr = Pick(data, existing, "apr");

        var pricing = CreateObject();
        SetProperty(pricing, "interestRate", interestRate);
        SetProperty(pricing, "insurancePremium", insurancePremium);
        SetProperty(pricing, "monthlyInstallment", monthlyInstallment);
        SetProperty(pricing, "apr", apr);

        var result = CreateObject();
        SetProperty(result, "pricing", pricing);

        LogInformation($"PriceLoanMapping: interestRate={interestRate}, monthlyInstallment={monthlyInstallment}, apr={apr}");

        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Tags = new[] { "pricing" }
        });
    }

    // Newly submitted value wins; otherwise keep whatever pricing already held.
    private static object Pick(IDictionary<string, object> data, IDictionary<string, object> existing, string key)
        => Get(data, key) ?? Get(existing, key);

    private static object Get(IDictionary<string, object> source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;
}
