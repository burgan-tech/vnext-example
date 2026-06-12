using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

/// <summary>
/// T3 (submit-assessment) order 1 — Script Task (type 7).
/// Computes a risk score and approved limit from the bureau result + requested amount,
/// merging assessor-confirmed values from the transition body into the master `assessment` section.
/// </summary>
public class ScoreAndLimitMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var body = context.Body;
        var data = context.Instance?.Data;

        var kkbScore = (int?)(data?.creditBureau?.kkbScore) ?? 0;
        var riskScore = body?.riskScore ?? (object)(kkbScore >= 1400 ? 85 : kkbScore >= 1000 ? 60 : 35);

        return Task.FromResult(new ScriptResponse
        {
            Data = new
            {
                assessment = new
                {
                    riskScore = riskScore,
                    approvedLimit = body?.approvedLimit ?? data?.application?.requestedAmount,
                    internalRating = body?.internalRating ?? (kkbScore >= 1400 ? "A" : kkbScore >= 1000 ? "B" : "C")
                }
            },
            Tags = new[] { "assessment", "scoring" }
        });
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse { Data = context.Body });
    }
}
