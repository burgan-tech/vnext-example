using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// T1 (submit-application) order 1 — Script Task (type 7).
/// The runtime merges the submit-application payload (loan-application schema) into instance
/// data at root level before the task runs, so this mapping never re-reads the request body:
/// it projects the root-level payload into the master `application` section and stamps an
/// applicationId. A Script Task has no endpoint to configure and its InputHandler result is
/// not persisted, so only OutputHandler produces instance data.
/// </summary>
public class ValidateApplicationMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Script Task: nothing to configure, nothing to persist.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var existing = Get(data, "application") as IDictionary<string, object>;

        var requestedAmount = Get(data, "requestedAmount");
        var isValid = (ToNumber(requestedAmount) ?? 0m) > 0m;

        var application = CreateObject();
        SetProperty(application, "applicationId", ToStr(Get(existing, "applicationId")) ?? NewApplicationId());
        SetProperty(application, "customerId", ToStr(Get(data, "customerId")));
        SetProperty(application, "productType", ToStr(Get(data, "productType")));
        SetProperty(application, "requestedAmount", requestedAmount);
        SetProperty(application, "currency", ToStr(Get(data, "currency")) ?? "TRY");
        SetProperty(application, "termMonths", Get(data, "termMonths"));
        SetProperty(application, "purpose", ToStr(Get(data, "purpose")));
        SetProperty(application, "monthlyIncome", Get(data, "monthlyIncome"));
        SetProperty(application, "validationStatus", isValid ? "valid" : "invalid");

        var result = CreateObject();
        SetProperty(result, "application", application);

        LogInformation($"ValidateApplicationMapping: customerId={ToStr(Get(data, "customerId"))}, requestedAmount={requestedAmount}, valid={isValid}");

        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Tags = new[] { "validation", isValid ? "success" : "failure" }
        });
    }

    private static string NewApplicationId() => $"APP-{Guid.NewGuid():N}".Substring(0, 16);

    private static object Get(IDictionary<string, object> source, string key)
        => source != null && source.TryGetValue(key, out var value) ? value : null;

    private static string ToStr(object value)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // Only used for comparisons — projected values are passed through with their original
    // JSON numeric type. System.Globalization is not available to the script sandbox.
    private static decimal? ToNumber(object value)
    {
        if (value == null) return null;
        try { return Convert.ToDecimal(value); } catch { return null; }
    }
}
