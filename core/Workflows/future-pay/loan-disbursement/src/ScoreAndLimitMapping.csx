using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;

/// <summary>
/// T3 (submit-assessment) order 1 — Script Task (type 7).
/// The submit-assessment payload (loan-assessment schema) is already merged into instance data
/// at root level, so this mapping reads the assessor-confirmed values from instance data,
/// falls back to values derived from the bureau result, and projects them into the master
/// `assessment` section from OutputHandler.
/// </summary>
public class ScoreAndLimitMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        // Script Task: nothing to configure, nothing to persist.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var data = context.Instance?.Data as IDictionary<string, object>;
        var creditBureau = Get(data, "creditBureau") as IDictionary<string, object>;
        var application = Get(data, "application") as IDictionary<string, object>;

        var kkbScore = ToNumber(Get(creditBureau, "kkbScore")) ?? 0m;

        // Assessor-confirmed values win; otherwise derive from the bureau score.
        var riskScore = Get(data, "riskScore")
            ?? (object)(kkbScore >= 1400m ? 85 : kkbScore >= 1000m ? 60 : 35);
        var internalRating = ToStr(Get(data, "internalRating"))
            ?? (kkbScore >= 1400m ? "A" : kkbScore >= 1000m ? "B" : "C");
        var approvedLimit = Get(data, "approvedLimit")
            ?? Get(application, "requestedAmount")
            ?? Get(data, "requestedAmount");

        var assessment = CreateObject();
        SetProperty(assessment, "riskScore", riskScore);
        SetProperty(assessment, "approvedLimit", approvedLimit);
        SetProperty(assessment, "internalRating", internalRating);

        var result = CreateObject();
        SetProperty(result, "assessment", assessment);

        LogInformation($"ScoreAndLimitMapping: kkbScore={kkbScore}, riskScore={riskScore}, approvedLimit={approvedLimit}, rating={internalRating}");

        return Task.FromResult(new ScriptResponse
        {
            Data = result,
            Tags = new[] { "assessment", "scoring" }
        });
    }

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
